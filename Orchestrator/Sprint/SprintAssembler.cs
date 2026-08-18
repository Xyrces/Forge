using System.Text.Json;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;
using Forge.Projects;

namespace Forge.Orchestrator.Sprint;

/// <summary>
/// Sprint flow — the fundamental execution model. ALL engineering
/// work happens inside a sprint: sprints group similar work into
/// goals and give agents shared memory/context within the sprint.
///
/// <para>Rules (operator, 2026-07-22):</para>
///
/// <list type="number">
    /// <item>Once a spec is accepted into an epic and groomed into
    /// stories + tasks, its tasks become ELIGIBLE for sprint ingest.
    /// Ad-hoc tasks (operator-enqueued, agent-filed follow-ups)
    /// become eligible only after technical grooming marks them
    /// (<c>groomed=true</c> metadata) — no task enters a sprint
    /// without grooming (operator rule, 2026-07-23).</item>
/// <item>The next sprint is assembled at the completion of the
/// last: when every task linked to the Active sprint is terminal
/// (Completed | Failed | Closed), the sprint is marked Completed
/// and the next one is assembled immediately.</item>
/// <item>Exactly one Active sprint per project. Dispatch only
/// claims tasks in the Active sprint (the gate lives in
/// OrchestratorAgent) — everything before that is design and
/// high-level planning.</item>
/// </list>
///
    /// <para>
    /// Assembly is deterministic and THEME-based (operator rule
    /// 2026-08-08 — there is exactly one kind of sprint): eligible
    /// tasks group by theme (spec chain → the groomed spec; follow-up
    /// chain → the followUpOf root ancestor; rootless → ad-hoc) and
    /// the highest-priority theme (then oldest) becomes the next
    /// sprint with ALL of that theme's tasks (follow-up themes capped
    /// at <see cref="MaxThemeTasks"/> per sprint). Only truly rootless
    /// ad-hoc tasks assemble one-per-sprint. Stories are linked too
    /// (progress display); completion counts non-container tasks only.
    /// </para>
///
/// <para>
/// Plain class (not a BackgroundService), fire-and-forget from
/// Program.cs like <see cref="ScheduledGroomer"/>. Best-effort:
/// exceptions abort the tick and retry next interval.
/// </para>
/// </summary>
public sealed class SprintAssembler
{
    private readonly ProjectContextFactory _projects;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<SprintAssembler> _logger;
    private readonly TimeSpan _interval;
    private readonly StageGates? _gates;

    public const string AdHocGroupName = "Ad-hoc work";
    private static readonly TimeSpan SprintDuration = TimeSpan.FromDays(14);

    public SprintAssembler(
        ProjectContextFactory projects,
        IDashboardEventBus events,
        ILogger<SprintAssembler> logger,
        TimeSpan? interval = null,
        StageGates? gates = null,
        Core.IFollowUpTriage? followUpTriage = null,
        WakeupSignal? wakeup = null,
        Core.Messaging.IEventPublisher? eventPublisher = null,
        TimeSpan? failureAgingWindow = null)
    {
        _projects = projects;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _gates = gates;
        _followUpTriage = followUpTriage;
        _wakeup = wakeup;
        _eventPublisher = eventPublisher;
        _failureAgingWindow = failureAgingWindow;
    }

    private readonly Core.IFollowUpTriage? _followUpTriage;
    private readonly WakeupSignal? _wakeup;
    private readonly Core.Messaging.IEventPublisher? _eventPublisher;
    private readonly TimeSpan? _failureAgingWindow;

    public TimeSpan Interval => _interval;

    // ---- Inter-sprint build-state snapshot (operator request
    // 2026-08-06) ----
    // The phase between sprint.completed and sprint.started (batch
    // triage → materialization → follow-up grooming) used to exist
    // only in journal logs — a completed sprint looked "stuck" on
    // the board. Every tick writes a snapshot to the project's
    // memory store under this key; GET /api/sprints/building reads
    // it back and the Sprints page renders it. Observability only:
    // writes are best-effort and never gate the tick.
    public const string BuildStateKey = Core.SprintBuildStateKeys.BuildStateKey;

    internal sealed record PendingGroomItem(string Id, string Title, DateTime CreatedAt);
    internal sealed record EligibleGroupItem(string Key, string Name, int TaskCount, int MinPriority, DateTime CreatedAt);
    internal sealed record HeldWorkItem(string Id, string Title, int AgeDays);
    internal sealed record SprintBuildState(
        string Phase,
        string Reason,
        DateTime UpdatedAt,
        string? CompletedSprintId,
        string? CompletedSprintName,
        IReadOnlyList<PendingGroomItem> PendingGroom,
        IReadOnlyList<EligibleGroupItem> EligibleGroups,
        string? ActiveSprintId,
        string? ActiveSprintName,
        int ActiveTotal,
        int ActiveTerminal,
        IReadOnlyList<HeldWorkItem>? HeldWork = null);

    private static readonly JsonSerializerOptions BuildStateJson = new(JsonSerializerDefaults.Web);

    private async Task WriteBuildStateAsync(IIssueStore issues, SprintBuildState state, CancellationToken ct)
    {
        try
        {
            if (issues is not Core.IssueStore concrete) return;
            var mem = new Core.MemoryStore(concrete.Db);
            await mem.RememberAsync(BuildStateKey, JsonSerializer.Serialize(state, BuildStateJson), ttlDays: null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SprintAssembler: build-state snapshot write failed (observability only)");
        }
    }

    private async Task<SprintBuildState?> ReadBuildStateAsync(IIssueStore issues, CancellationToken ct)
    {
        try
        {
            if (issues is not Core.IssueStore concrete) return null;
            var mem = new Core.MemoryStore(concrete.Db);
            var hit = (await mem.RecallAsync(BuildStateKey, ct))
                .FirstOrDefault(m => string.Equals(m.Key, BuildStateKey, StringComparison.Ordinal));
            return hit is null ? null : JsonSerializer.Deserialize<SprintBuildState>(hit.Body, BuildStateJson);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SprintAssembler: build-state snapshot read failed (observability only)");
            return null;
        }
    }

    private static SprintBuildState EmptyState(
        string phase, string reason, string? completedId, string? completedName) =>
        new(phase, reason, DateTime.UtcNow, completedId, completedName,
            Array.Empty<PendingGroomItem>(), Array.Empty<EligibleGroupItem>(), null, null, 0, 0);

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger the first tick so we don't fight startup recovery.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }

        // Message-driven: trigger events kick via the wakeup signal;
        // the backstop interval re-derives everything if hints are
        // lost. The 5m PeriodicTimer is gone.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembler tick failed; continuing");
            }
            if (!await WaitForNextTickAsync(ct)) break;
        }
    }

    /// <summary>Wait for a trigger-event kick or the backstop interval.
    /// Returns false on shutdown. Without a signal wired (tests) falls
    /// back to the plain interval delay.</summary>
    private async Task<bool> WaitForNextTickAsync(CancellationToken ct)
    {
        if (_wakeup is null)
        {
            try
            {
                await Task.Delay(_interval, ct);
                return true;
            }
            catch (OperationCanceledException) { return false; }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_interval);
        try
        {
            await _wakeup.WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return !ct.IsCancellationRequested;
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var project in _projects.KnownProjects)
        {
            if (ct.IsCancellationRequested) return;
            var ctx = _projects.Find(project.Id);
            if (ctx is null) continue;
            try
            {
                await TickProjectAsync(project.Id, ctx.Issues, ctx.Sprints, ctx.Specs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SprintAssembler: tick failed for project {ProjectId}; continuing", project.Id);
            }
        }
    }

    /// <summary>
    /// One project pass: complete the Active sprint when its tasks
    /// are all terminal, then assemble + activate the next one from
    /// eligible work. Exposed for tests.
    /// </summary>
    internal async Task TickProjectAsync(
        string projectId, IIssueStore issues, ISprintStore sprints, ISpecStore specs, CancellationToken ct)
    {
        var prevState = await ReadBuildStateAsync(issues, ct);
        // Aged-failure triage FIRST: a sprint whose last open task is
        // an ancient Failed would otherwise sit Active forever, and a
        // backlog held hostage by dead failures starves assembly
        // silently (operator direction 2026-08-18: "fix this
        // permanently"). Fresh failures stay untouched — the
        // no-auto-clear rule is about the operator investigating
        // RECENT failures; past the aging window the task is
        // definitionally abandoned. The closure cascades through
        // CloseTerminalEpicsAsync below (stories/epics auto-close
        // behind it).
        await SweepAgedFailuresAsync(projectId, issues, ct);
        string? completedSprintId = null;
        string? completedSprintName = null;
        var active = await sprints.GetActiveAsync(ct);
        if (active is not null)
        {
            var (complete, total, terminal) = await CompletionProgressAsync(active, issues, sprints, ct);
            if (!complete)
            {
                // Ad-hoc injection (operator rule 2026-07-27): a
                // groomed ad-hoc task joins the ACTIVE sprint when it
                // BELONGS there — it is part of the same work (its
                // followUpOf chain reaches a sprint member) or it
                // enables/unblocks the ongoing work (a blocks edge to
                // a member, or an operator P1/blocker flag). Unrelated
                // groomed ad-hoc work gets its own solo sprint at
                // assembly instead.
                await InjectAdHocAsync(active, projectId, issues, sprints, ct);
                await WriteBuildStateAsync(issues, new SprintBuildState(
                    Phase: "running",
                    Reason: $"Sprint '{active.Name}' in progress — {terminal}/{total} tasks terminal",
                    UpdatedAt: DateTime.UtcNow,
                    CompletedSprintId: null, CompletedSprintName: null,
                    PendingGroom: Array.Empty<PendingGroomItem>(),
                    EligibleGroups: Array.Empty<EligibleGroupItem>(),
                    ActiveSprintId: active.Id, ActiveSprintName: active.Name,
                    ActiveTotal: total, ActiveTerminal: terminal), ct);
                return;
            }
            await sprints.UpdateAsync(active.Id,
                new Dictionary<string, object?> { ["status"] = nameof(SprintStatus.Completed) }, ct);
            _logger.LogInformation("Sprint {SprintId} ({Name}) completed — all member tasks terminal (project={Project})",
                active.Id, active.Name, projectId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintCompleted,
                null, $"Sprint '{active.Name}' completed"));
            completedSprintId = active.Id;
            completedSprintName = active.Name;

            // Materialize tracked follow-ups (operator model
            // 2026-07-31): drafts filed DURING the sprint become real
            // work only now — the work they reference is merged and
            // canonical. The batch triage (when wired) merges dupes,
            // groups epics, and discards junk first; invalid or
            // unavailable triage falls back to 1:1. Either way the
            // results go through grooming before the next sprint
            // assembles (the gate below).
            var draftStore = new Core.FollowUpDraftStore((Core.IssueStore)issues);
            var unconsumed = await draftStore.ListUnconsumedAsync(ct);
            var materialized = await MaterializeFollowUpsAsync(projectId, issues, specs, draftStore, unconsumed, ct);
            if (materialized > 0)
            {
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintMaterialized,
                    null, $"{materialized} work item(s) materialized from '{active.Name}' follow-ups",
                    new Dictionary<string, object?>
                    {
                        ["projectId"] = projectId,
                        ["created"] = materialized,
                        ["drafts"] = unconsumed.Count,
                    }));
            }
        }

        // Epic lifecycle: close epics whose entire tree is terminal
        // (they otherwise linger as Pending on the board forever).
        await CloseTerminalEpicsAsync(issues, specs, ct);

        // Operator gate: completing a finished sprint is bookkeeping
        // (always allowed); STARTING new work is the gated decision.
        if (_gates is not null && await _gates.IsHeldAsync(StageGates.Sprint, ct))
        {
            _logger.LogInformation("Sprint assembly held by operator gate (project={Project})", projectId);
            var heldGroups = await SummarizeEligibleGroupsAsync(projectId, issues, sprints, specs, ct);
            await WriteBuildStateAsync(issues, new SprintBuildState(
                Phase: "held",
                Reason: "Sprint assembly held by operator gate",
                UpdatedAt: DateTime.UtcNow,
                CompletedSprintId: completedSprintId, CompletedSprintName: completedSprintName,
                PendingGroom: Array.Empty<PendingGroomItem>(),
                EligibleGroups: heldGroups,
                ActiveSprintId: null, ActiveSprintName: null, ActiveTotal: 0, ActiveTerminal: 0), ct);
            return;
        }

        // Follow-up grooming gate (operator model 2026-07-31): the
        // next sprint does NOT start until the materialized
        // follow-ups are resolved (groomed or closed by the ad-hoc
        // groomer pass).
        var pendingFollowUpRows = (await issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct))
            .Where(i => i.GetMetadata("followUpOf") is not null
                && !string.Equals(i.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.CreatedAt)
            .ToList();
        if (pendingFollowUpRows.Count > 0)
        {
            var reason = $"Sprint assembly waiting: {pendingFollowUpRows.Count} materialized follow-ups awaiting grooming";
            _logger.LogInformation("{Reason} (project={Project})", reason, projectId);
            var pendingItems = pendingFollowUpRows
                .Select(i => new PendingGroomItem(i.Id, i.Title, i.CreatedAt))
                .ToList();
            var waitingGroups = await SummarizeEligibleGroupsAsync(projectId, issues, sprints, specs, ct);
            // Publish on CHANGE only — the tick runs every 5 minutes
            // and a long groom would otherwise spam the feed with
            // identical "waiting" entries.
            if (prevState?.Phase != "awaiting-groom"
                || !prevState.PendingGroom.Select(p => p.Id).SequenceEqual(pendingItems.Select(p => p.Id)))
            {
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintAssemblyWaiting,
                    null, $"Next sprint waiting on grooming: {pendingItems.Count} follow-up(s)",
                    new Dictionary<string, object?>
                    {
                        ["projectId"] = projectId,
                        ["count"] = pendingItems.Count,
                    }));
            }
            await WriteBuildStateAsync(issues, new SprintBuildState(
                Phase: "awaiting-groom",
                Reason: reason,
                UpdatedAt: DateTime.UtcNow,
                CompletedSprintId: completedSprintId, CompletedSprintName: completedSprintName,
                PendingGroom: pendingItems,
                EligibleGroups: waitingGroups,
                ActiveSprintId: null, ActiveSprintName: null, ActiveTotal: 0, ActiveTerminal: 0), ct);
            return;
        }

        var assembled = await AssembleNextAsync(projectId, issues, sprints, specs, ct);
        if (assembled is not null)
        {
            await WriteBuildStateAsync(issues, new SprintBuildState(
                Phase: "running",
                Reason: $"Sprint '{assembled.Value.Name}' assembled + activated",
                UpdatedAt: DateTime.UtcNow,
                CompletedSprintId: null, CompletedSprintName: null,
                PendingGroom: Array.Empty<PendingGroomItem>(),
                EligibleGroups: Array.Empty<EligibleGroupItem>(),
                ActiveSprintId: assembled.Value.Id, ActiveSprintName: assembled.Value.Name,
                ActiveTotal: assembled.Value.TaskCount, ActiveTerminal: 0), ct);
        }
        else
        {
            // Starvation visibility (2026-08-18): "no eligible work"
            // is ambiguous when the backlog is full of zombie cards
            // held open by Failed tasks — the operator sees a completed
            // sprint + a busy board and concludes the pipeline died.
            // Name the blockage: how many groups are held, by how many
            // failures, and when the aging sweep clears them.
            var held = await SummarizeHeldWorkAsync(issues, ct);
            if (held.FailedTasks == 0)
            {
                await WriteBuildStateAsync(issues, EmptyState(
                    "idle", "No eligible work in the backlog — nothing to assemble",
                    completedSprintId, completedSprintName), ct);
            }
            else
            {
                var windowNote = _failureAgingWindow is { } w
                    ? $"failures auto-close after {w.TotalDays:0} days without operator action"
                    : "failure aging is disabled — requeue or close Failed tasks to unblock";
                var reason =
                    $"No eligible work — {held.HeldGroups} group(s) held by {held.FailedTasks} Failed task(s); " +
                    windowNote;
                if (prevState?.Phase != "starved"
                    || !(prevState.HeldWork ?? Array.Empty<HeldWorkItem>()).Select(h => h.Id).SequenceEqual(held.Items.Select(h => h.Id)))
                {
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, "sprint.assembly.starved",
                        null, $"Next sprint blocked: {held.FailedTasks} Failed task(s) holding {held.HeldGroups} group(s)",
                        new Dictionary<string, object?>
                        {
                            ["projectId"] = projectId,
                            ["failedTasks"] = held.FailedTasks,
                            ["heldGroups"] = held.HeldGroups,
                        }));
                    _logger.LogInformation("Sprint assembly starved for project {Project}: {Reason}", projectId, reason);
                }
                await WriteBuildStateAsync(issues, new SprintBuildState(
                    Phase: "starved",
                    Reason: reason,
                    UpdatedAt: DateTime.UtcNow,
                    CompletedSprintId: completedSprintId, CompletedSprintName: completedSprintName,
                    PendingGroom: Array.Empty<PendingGroomItem>(),
                    EligibleGroups: Array.Empty<EligibleGroupItem>(),
                    ActiveSprintId: null, ActiveSprintName: null, ActiveTotal: 0, ActiveTerminal: 0,
                    HeldWork: held.Items), ct);
            }
        }
    }

    /// <summary>
    /// Close Failed tasks older than the aging window (operator
    /// direction 2026-08-18): a failure nobody requeued or closed
    /// within the window is abandoned work, and leaving it Failed
    /// holds its story/epic (and sprint assembly) hostage forever —
    /// observed live 2026-08-17/18: porthorizon sat idle 24h+ on 20
    /// Failed tasks aged 8-17 days while the board read as "busy
    /// backlog". Fresh failures are NEVER touched (the no-auto-clear
    /// rule protects the operator's active investigation). Returns the
    /// number swept.
    /// </summary>
    private async Task<int> SweepAgedFailuresAsync(string projectId, IIssueStore issues, CancellationToken ct)
    {
        if (_failureAgingWindow is not { } window) return 0;
        var cutoff = DateTime.UtcNow - window;
        var failed = await issues.ListAsync(new IssueFilter { Status = IssueStatus.Failed }, ct);
        var aged = failed.Where(i => i.UpdatedAt <= cutoff).OrderBy(i => i.UpdatedAt).ToList();
        foreach (var task in aged)
        {
            var ageDays = (DateTime.UtcNow - task.UpdatedAt).TotalDays;
            await issues.TransitionAsync(task.Id, IssueStatus.Closed,
                $"auto-closed: abandoned failure — Failed {ageDays:0.#} days with no operator action " +
                $"(aging window {window.TotalDays:0} days). Requeue or re-enqueue to revive the work.", ct: ct);
            _logger.LogInformation(
                "Aged failure auto-closed: {Id} (Failed {Age:0.#}d, window {Window:0}d, project={Project})",
                task.Id, ageDays, window.TotalDays, projectId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, "sprint.failure.swept",
                task.Id, $"{task.Id} auto-closed (Failed {ageDays:0.#}d)",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["taskId"] = task.Id,
                    ["ageDays"] = ageDays,
                }));
        }
        return aged.Count;
    }

    /// <summary>Work the operator sees as "backlog" that assembly can
    /// never touch: Failed tasks plus the Pending containers (story/
    /// epic) with at least one Failed/Blocked descendant.</summary>
    private static async Task<(int HeldGroups, int FailedTasks, IReadOnlyList<HeldWorkItem> Items)>
        SummarizeHeldWorkAsync(IIssueStore issues, CancellationToken ct)
    {
        var all = await issues.ListAsync(new IssueFilter(), ct);
        var failed = all.Where(i => i.Status is IssueStatus.Failed or IssueStatus.Blocked).ToList();
        if (failed.Count == 0) return (0, 0, Array.Empty<HeldWorkItem>());
        var byParent = all.Where(i => i.ParentIssueId is not null)
            .GroupBy(i => i.ParentIssueId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var heldContainers = all.Count(i =>
            i.Type is "story" or "epic"
            && i.Status is IssueStatus.Pending or IssueStatus.InProgress
            && byParent.TryGetValue(i.Id, out var kids)
            && kids.Any(k => k.Status is IssueStatus.Failed or IssueStatus.Blocked));
        var items = failed
            .OrderBy(i => i.UpdatedAt)
            .Take(20)
            .Select(i => new HeldWorkItem(i.Id, i.Title,
                (int)(DateTime.UtcNow - i.UpdatedAt).TotalDays))
            .ToList();
        return (heldContainers, failed.Count, items);
    }

    /// <summary>
    /// Completion progress: every linked non-container issue counts;
    /// stories linked for progress display don't gate completion.
    /// Failed blocks completion — operator rule 2026-07-25: don't
    /// auto-clear Failed; the operator must investigate or
    /// requeue. Sprint 5 of talaria dropped a Failed task onto the
    /// floor in 2026-08-11 because Failed was counted as terminal
    /// — Failed must stay open and keep the sprint active.
    /// </summary>
    private static async Task<(bool Complete, int Total, int Terminal)> CompletionProgressAsync(
        SprintRecord sprint, IIssueStore issues, ISprintStore sprints, CancellationToken ct)
    {
        var memberIds = await sprints.GetIssueIdsAsync(sprint.Id, ct);
        var total = 0;
        var terminal = 0;
        foreach (var id in memberIds)
        {
            var issue = await issues.GetAsync(id, ct);
            if (issue is null || AgentTaskTypes.IsContainer(issue.Type)
                || issue.Type == AgentTaskTypes.PrWatch)
            {
                continue;
            }
            total++;
            // Failed does NOT count as terminal: the operator must
            // investigate or requeue (rule 2026-07-25; Sprint 5 of
            // talaria dropped a Failed task onto the floor in
            // 2026-08-11). Completed | Closed are the only true
            // finishes.
            if (issue.Status is IssueStatus.Completed or IssueStatus.Closed)
            {
                terminal++;
            }
        }
        // Empty sprint (no non-container tasks, e.g. only stories
        // linked) is complete — defensive, so an empty Active doesn't
        // block assembly forever. A sprint with tasks is NOT complete
        // until all of them are Completed/Closed.
        return (total == 0 || terminal == total, total, terminal);
    }

    /// <summary>
    /// What the assembler would pick from on the next assembly: the
    /// eligible groups (one per groomed spec, plus ad-hoc) in claim
    /// order — priority first, then oldest. Powers the "up next"
    /// section of the build-state snapshot so the board shows what's
    /// queued BEHIND a grooming wait or operator gate.
    /// </summary>
    private async Task<IReadOnlyList<EligibleGroupItem>> SummarizeEligibleGroupsAsync(
        string projectId, IIssueStore issues, ISprintStore sprints, ISpecStore specs, CancellationToken ct)
    {
        try
        {
            var eligible = await ListEligibleAsync(issues, sprints, ct);
            if (eligible.Count == 0) return Array.Empty<EligibleGroupItem>();
            var byId = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);
            var groups = new Dictionary<string, List<IssueRecord>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var task in eligible)
            {
                var key = ResolveThemeKey(task, byId);
                if (groups.TryGetValue(key, out var list))
                {
                    list.Add(task);
                }
                else
                {
                    groups[key] = new List<IssueRecord> { task };
                    order.Add(key);
                }
            }
            await DropCrossProjectGroupsAsync(groups, order, projectId, specs, _logger, ct);
            var items = new List<EligibleGroupItem>();
            foreach (var key in order)
            {
                var members = groups[key];
                // Ad-hoc assembles one SOLO sprint per task named
                // after the task — show the task that would go first.
                // Follow-up themes name after the LEADING MEMBER too
                // (the chain root is usually merged already — see
                // DescribeAsync), not the root task's title.
                var name = key == AdHocGroupName
                    ? members.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).First().Title
                    : key.StartsWith(FollowUpThemePrefix, StringComparison.Ordinal)
                        ? members.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).First().Title
                        : (await specs.GetAsync(key, ct))?.Title ?? key;
                items.Add(new EligibleGroupItem(key, name, members.Count,
                    members.Min(t => t.Priority), members.Min(t => t.CreatedAt)));
            }
            return items
                .OrderBy(g => g.MinPriority)
                .ThenBy(g => g.CreatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SprintAssembler: eligible-group summary failed (observability only)");
            return Array.Empty<EligibleGroupItem>();
        }
    }

    /// <summary>
    /// Turn a completed sprint's tracked follow-up drafts into real
    /// work. With a triage wired: one batch pass validates + applies
    /// (create/merge/epic/discard); items citing unknown drafts are
    /// dropped, drafts the triage never cited are 1:1-materialized —
    /// the agent shapes, it can never invent or lose work. Without a
    /// triage (or when it returns null): plain 1:1. Returns the
    /// number of work items created.
    /// </summary>
    private async Task<int> MaterializeFollowUpsAsync(
        string projectId, IIssueStore issues, ISpecStore specs,
        Core.FollowUpDraftStore draftStore, IReadOnlyList<Core.FollowUpDraft> unconsumed, CancellationToken ct)
    {
        if (unconsumed.Count == 0) return 0;

        Core.FollowUpTriageDecision? decision = null;
        if (_followUpTriage is not null && unconsumed.Count >= 2)
        {
            decision = await _followUpTriage.TriageAsync(projectId, unconsumed, ct);
            if (decision is null)
            {
                _logger.LogWarning("Sprint materialization: triage unavailable — falling back to 1:1 ({Count} drafts, project={Project})",
                    unconsumed.Count, projectId);
            }
            else
            {
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintTriageCompleted,
                    null, $"Follow-up triage: {decision.Items.Count} disposition(s) over {unconsumed.Count} draft(s)",
                    new Dictionary<string, object?>
                    {
                        ["projectId"] = projectId,
                        ["drafts"] = unconsumed.Count,
                        ["items"] = decision.Items.Count,
                    }));
            }
        }

        var created = 0;
        var cited = new HashSet<long>();
        if (decision is not null)
        {
            var byId = unconsumed.ToDictionary(d => d.Id);
            foreach (var item in decision.Items)
            {
                var sources = item.SourceDraftIds.Where(byId.ContainsKey).ToList();
                if (sources.Count == 0)
                {
                    _logger.LogWarning("Sprint materialization: triage item cites unknown drafts [{Ids}] — dropped",
                        string.Join(",", item.SourceDraftIds));
                    continue;
                }
                cited.UnionWith(sources);
                switch (item.Action)
                {
                    case "create":
                    case "merge":
                    {
                        var src = sources.Select(id => byId[id]).ToList();
                        var task = await issues.CreateAsync(new NewIssue(
                            Type: "task",
                            Title: item.Title ?? src[0].Title,
                            Description: item.Description
                                ?? string.Join("\n\n---\n\n", src.Select(d => $"[draft {d.Id}] {d.Description}")),
                            Priority: item.Priority ?? src.Min(d => d.Priority),
                            Metadata: new Dictionary<string, object>
                            {
                                ["source"] = src[0].SourceRole,
                                ["followUpOf"] = src[0].SourceIssueId,
                                ["fromDraft"] = string.Join(",", sources),
                            }), ct);
                        foreach (var id in sources)
                        {
                            await draftStore.SetDispositionAsync(id,
                                item.Action == "merge" ? "merged" : "materialized", task.Id, ct);
                        }
                        created++;
                        break;
                    }
                    case "epic":
                    {
                        var src = sources.Select(id => byId[id]).ToList();
                        var spec = await specs.CreateAsync(new Core.NewSpec(
                            ProjectId: projectId,
                            Title: item.Title ?? $"Follow-up theme from {src.Count} findings",
                            Body: item.Description
                                ?? string.Join("\n\n---\n\n", src.Select(d => $"[draft {d.Id}] {d.Description}"))), ct);
                        await specs.SetStatusAsync(spec.Id, Core.SpecStatus.Approved, ct);
                        foreach (var id in sources)
                        {
                            await draftStore.SetDispositionAsync(id, "epic", spec.Id, ct);
                        }
                        created++;
                        break;
                    }
                    case "discard":
                    {
                        foreach (var id in sources)
                        {
                            await draftStore.SetDispositionAsync(id, "discarded",
                                item.Reason ?? "triaged as junk", ct);
                        }
                        break;
                    }
                }
            }
        }

        // Fallback + coverage: 1:1 for anything the triage didn't
        // cite (or all of it when no triage ran).
        var remaining = unconsumed.Where(d => !cited.Contains(d.Id)).ToList();
        foreach (var draft in remaining)
        {
            var task = await issues.CreateAsync(new NewIssue(
                Type: "task",
                Title: draft.Title,
                Description: draft.Description,
                Priority: draft.Priority,
                Metadata: new Dictionary<string, object>
                {
                    ["source"] = draft.SourceRole,
                    ["followUpOf"] = draft.SourceIssueId,
                    ["fromDraft"] = draft.Id.ToString(),
                }), ct);
            await draftStore.SetDispositionAsync(draft.Id, "materialized", task.Id, ct);
            created++;
        }

        _logger.LogInformation(
            "Sprint materialization: {Created} work items from {Drafts} drafts (triaged={Triaged}, project={Project})",
            created, unconsumed.Count, decision is not null, projectId);
        return created;
    }

    /// <summary>
    /// Inject groomed ad-hoc tasks that BELONG to the active sprint
    /// (operator rule 2026-07-27). A sprint is a coherent deployable
    /// unit — ad-hoc work may join it, but only when it is part of
    /// the same work or it enables/unblocks it. Three triggers,
    /// all requiring groomed=true:
    /// <list type="number">
    /// <item><b>Same work</b>: the task's followUpOf chain reaches a
    /// member of the active sprint (a follow-up filed from the
    /// sprint's own work).</item>
    /// <item><b>Unblocks</b>: a <c>blocks</c> dependency edge has the
    /// task blocking an active sprint member (the member cannot
    /// proceed until it lands — e.g. the merge-gate harness fix).</item>
    /// <item><b>Operator-urgent</b>: priority 1 or metadata
    /// blocker=true (the operator's explicit inject signal).</item>
    /// </list>
    /// Injection never completes, replaces, or reorders the sprint.
    /// </summary>
    private async Task InjectAdHocAsync(
        SprintRecord active, string projectId, IIssueStore issues, ISprintStore sprints, CancellationToken ct)
    {
        var pending = await issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct);
        var memberIds = (await sprints.GetIssueIdsAsync(active.Id, ct)).ToHashSet(StringComparer.Ordinal);
        var all = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);
        var blockersOfMembers = await issues.ListBlockersOfAsync(memberIds, ct);

        foreach (var task in pending)
        {
            if (AgentTaskTypes.IsContainer(task.Type) || task.Type == AgentTaskTypes.PrWatch) continue;
            if (task.ParentIssueId is not null) continue;   // spec-chain work flows through assembly
            if (memberIds.Contains(task.Id)) continue;
            if (!string.Equals(task.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase)) continue;

            var reason = InjectionReason(task, memberIds, all, blockersOfMembers);
            if (reason is null) continue;

            await sprints.AddIssueAsync(active.Id, task.Id, ct);
            _logger.LogInformation(
                "Sprint {SprintId}: injected ad-hoc task {TaskId} ({Title}) — {Reason}",
                active.Id, task.Id, task.Title, reason);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
                task.Id, $"Injected into active sprint '{active.Name}' — {reason}"));
            // Membership writes publish nothing on their own — kick
            // dispatch so the injection is claimed now, not at the
            // backstop.
            if (_eventPublisher is not null)
            {
                var kickedAt = DateTimeOffset.UtcNow;
                await _eventPublisher.PublishAsync(new Core.Messaging.TaskEnqueued
                {
                    MessageId = Core.Messaging.TaskEnqueued.IdFor(projectId, task.Id, kickedAt),
                    ProjectId = projectId,
                    TaskId = task.Id,
                    TaskType = task.Type,
                    EnqueuedAt = kickedAt,
                }, ct);
            }
        }
    }

    private static string? InjectionReason(
        IssueRecord task,
        HashSet<string> memberIds,
        Dictionary<string, IssueRecord> all,
        HashSet<string> blockersOfMembers)
    {
        // Unblocks: a blocks edge has this task blocking a sprint
        // member — the member cannot proceed until it lands.
        // (Operator model 2026-07-31: this is the ONLY organic
        // injection path — followUpOf-chain "same work" injection was
        // removed; follow-ups now materialize at sprint completion
        // and join a LATER sprint through grooming.)
        if (blockersOfMembers.Contains(task.Id))
        {
            return "unblocks ongoing work";
        }
        // Operator-urgent.
        if (task.Priority == 1
            || string.Equals(task.GetMetadata("blocker"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return "operator-flagged blocker";
        }
        // Operator requeue: an ad-hoc task the operator explicitly
        // requeued from Failed/Blocked carries operator intent to
        // run — inject it (otherwise ad-hoc work never assembles and
        // the sanctioned requeue path would strand it forever).
        if (task.GetMetadata("requeuedFromFailedAt") is not null)
        {
            return "operator requeue";
        }
        return null;
    }

    /// <summary>
    /// Close epics whose entire tree is terminal (EpicCompletion
    /// rule): spec(s) past grooming, all stories/tasks
    /// Completed/Closed, no Failed/Blocked descendants, no live
    /// watch. Epics have no other lifecycle — without this they
    /// linger as Pending on the board forever (observed 2026-07-27:
    /// epics 6-11 all Pending despite fully merged work).
    /// Idempotent; Failed/Blocked descendants keep the epic open for
    /// the operator (the no-auto-clear rule).
    /// </summary>
    private async Task CloseTerminalEpicsAsync(IIssueStore issues, ISpecStore specs, CancellationToken ct)
    {
        var all = await issues.ListAsync(new IssueFilter(), ct);

        // Story lifecycle first (observed 2026-07-27: stories stay
        // Pending forever because tasks complete but nothing
        // transitions the story — which in turn keeps the epic
        // open). A story closes when every task under it is
        // Completed/Closed and none is Failed/Blocked (operator
        // decision). Stories with no tasks yet stay open.
        var stories = all.Where(i =>
            i.Type == "story" && i.Status is IssueStatus.Pending or IssueStatus.InProgress).ToList();
        foreach (var story in stories)
        {
            var tasks = all.Where(i => i.Type == "task" && i.ParentIssueId == story.Id).ToList();
            if (tasks.Count == 0) continue;
            if (tasks.Any(t => t.Status is IssueStatus.Failed or IssueStatus.Blocked)) continue;
            if (tasks.Any(t => t.Status is not (IssueStatus.Completed or IssueStatus.Closed))) continue;
            await issues.TransitionAsync(story.Id, IssueStatus.Closed,
                "auto-closed: all tasks terminal", ct: ct);
            _logger.LogInformation("Story {Id} auto-closed (all tasks terminal)", story.Id);
            // Keep the in-memory view current for the epic pass below.
            all = all.Select(i => i.Id == story.Id ? i with { Status = IssueStatus.Closed } : i).ToList();
        }

        var epics = all.Where(i =>
            i.Type == "epic" && i.Status is IssueStatus.Pending or IssueStatus.InProgress).ToList();
        if (epics.Count == 0) return;
        var allSpecs = await specs.ListAsync(projectId: null, status: null, ct);

        foreach (var epic in epics)
        {
            var specsForEpic = allSpecs.Where(s =>
                string.Equals(s.ParentIssueId, epic.Id, StringComparison.Ordinal)).ToList();
            var decision = Core.EpicCompletion.Evaluate(epic, specsForEpic, all);
            if (!decision.ShouldClose)
            {
                _logger.LogDebug("Epic {Id} stays open: {Reason}", epic.Id, decision.Reason);
                continue;
            }
            await issues.TransitionAsync(epic.Id, IssueStatus.Closed,
                $"auto-closed: {decision.Reason}", ct: ct);
            _logger.LogInformation("Epic {Id} auto-closed ({Reason})", epic.Id, decision.Reason);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
                epic.Id, "Epic auto-closed — all work terminal"));
        }
    }

    /// <summary>
    /// The assembly candidate pool: Pending, non-container, non-watch,
    /// not already linked to an ACTIVE sprint (never double-stage work
    /// that's already on a live board), no open blockers (operator
    /// report 2026-07-31: a task with an OPEN blocker must not
    /// assemble — without its blocker in the same sprint it sits
    /// undispatchable forever and the sprint can never complete;
    /// observed live: task-348 stalled Sprint 15), and either
    /// spec-chained or groomed ad-hoc (no task enters a sprint
    /// without grooming). Completed-sprint membership is NOT
    /// disqualifying: a sprint only completes when every member is
    /// terminal, so a Pending task whose sole membership is a
    /// Completed sprint is definitionally an operator requeue —
    /// excluding it would strand requeued work forever (observed
    /// live 2026-07-24: task-158).
    /// </summary>
    internal static async Task<List<IssueRecord>> ListEligibleAsync(
        IIssueStore issues, ISprintStore sprints, CancellationToken ct)
    {
        var all = await issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct);
        var byId = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);
        var sprinted = new HashSet<string>();
        foreach (var s in await sprints.ListAsync(activeOnly: true, ct))
        {
            foreach (var id in await sprints.GetIssueIdsAsync(s.Id, ct))
            {
                sprinted.Add(id);
            }
        }

        var eligible = all.Where(i =>
                !AgentTaskTypes.IsContainer(i.Type)
                && i.Type != AgentTaskTypes.PrWatch
                && !sprinted.Contains(i.Id))
            .ToList();

        var openBlockers = await issues.OpenBlockersAsync(eligible.Select(i => i.Id).ToList(), ct);
        eligible = eligible.Where(t => !openBlockers.ContainsKey(t.Id)).ToList();

        return eligible
            .Where(t => ResolveGroupKey(t, byId) != AdHocGroupName
                || string.Equals(t.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<(string Id, string Name, int TaskCount)?> AssembleNextAsync(
        string projectId, IIssueStore issues, ISprintStore sprints, ISpecStore specs, CancellationToken ct)
    {
        var eligible = await ListEligibleAsync(issues, sprints, ct);
        if (eligible.Count == 0) return null;
        var byId = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);

        // Group by THEME (operator rule 2026-08-08): spec-chained
        // tasks under their spec, follow-ups under their followUpOf
        // root (one sprint per theme — never a solo follow-up
        // sprint), rootless ad-hoc under the singleton ad-hoc key.
        var groups = new Dictionary<string, List<IssueRecord>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        foreach (var task in eligible)
        {
            var groupKey = ResolveThemeKey(task, byId);
            if (groups.TryGetValue(groupKey, out var list))
            {
                list.Add(task);
            }
            else
            {
                groups[groupKey] = new List<IssueRecord> { task };
                groupOrder.Add(groupKey);
            }
        }

        // Cross-project guard: a spec group belongs to the project
        // that OWNS the spec (see DropCrossProjectGroupsAsync).
        await DropCrossProjectGroupsAsync(groups, groupOrder, projectId, specs, _logger, ct);
        if (groupOrder.Count == 0) return null;

        // Resolve each group's display name / goal / age for ordering.
        // Spec groups: name = spec title, goal = parent epic's
        // description (the epic is the spec's parent_issue_id) or the
        // spec title. Oldest group wins; ad-hoc sorts last so real
        // pipeline work always goes first.
        async Task<(string Name, string Goal, DateTime CreatedAt)> DescribeAsync(string key)
        {
            if (key == AdHocGroupName)
            {
                return (AdHocGroupName,
                    "Operator-enqueued work not tied to a spec epic.",
                    DateTime.MaxValue);
            }
            if (key.StartsWith(FollowUpThemePrefix, StringComparison.Ordinal)
                && groups.TryGetValue(key, out var cluster) && cluster.Count > 0)
            {
                var rootId = key[FollowUpThemePrefix.Length..];
                var rootTitle = byId.TryGetValue(rootId, out var rootTask) ? rootTask.Title : rootId;
                // Name after the LEADING MEMBER, not the root: the
                // root is usually long-merged (the follow-up chain
                // outlives it), and a sprint named for a task it does
                // not contain reads as work happening OUTSIDE the
                // sprint (operator confusion 2026-08-18: Sprint 130
                // was named 'Fix stale inline comment near
                // MarkConsumed' — the completed root task-660 — while
                // its only member was task-705's docs wording fix).
                // The root stays in the goal for chain context.
                var ordered = cluster.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).ToList();
                var memberTitles = string.Join("; ", ordered.Take(3).Select(t => t.Title));
                var goal = $"Follow-up work filed from {rootId} ({rootTitle}): {memberTitles}" +
                    (cluster.Count > 3 ? $" (+{cluster.Count - 3} more)" : "");
                return (ordered[0].Title, goal, cluster.Min(t => t.CreatedAt));
            }
            var spec = await specs.GetAsync(key, ct);
            var epicDesc = spec?.ParentIssueId is not null
                && byId.TryGetValue(spec.ParentIssueId, out var epic)
                && !string.IsNullOrWhiteSpace(epic.Description)
                    ? epic.Description!
                    : null;
            // Fallback chain when the spec read misses (transient
            // version-bump race): the parent STORY's title is still a
            // meaningful short goal — a raw spec id never is.
            var storyTitle = groups.TryGetValue(key, out var members) && members.Count > 0
                && members[0].ParentIssueId is not null
                && byId.TryGetValue(members[0].ParentIssueId!, out var story)
                    ? story.Title
                    : null;
            return (spec?.Title ?? storyTitle ?? key,
                epicDesc ?? $"Complete all groomed tasks for {spec?.Title ?? storyTitle ?? key}.",
                spec?.CreatedAt ?? DateTime.MaxValue);
        }

        var described = new Dictionary<string, (string Name, string Goal, DateTime CreatedAt)>();
        foreach (var key in groupOrder)
        {
            described[key] = await DescribeAsync(key);
        }
        // Assembly ordering (operator direction 2026-07-31): priority
        // FIRST (the group's highest-priority member), then oldest.
        // Ad-hoc work participates in the same ordering — no longer
        // forced behind spec groups.
        var chosenKey = groupOrder
            .OrderBy(k => groups[k].Min(t => t.Priority))
            .ThenBy(k => described[k].CreatedAt)
            .First();
        var chosen = groups[chosenKey];
        var (name, goal, _) = described[chosenKey];

        // Rootless ad-hoc assembly is ALWAYS a solo sprint (highest
        // priority first, then oldest) — never a bundle. Follow-up
        // themes pack up to MaxThemeTasks per sprint (priority, then
        // oldest); the remainder stays eligible for the next sprint.
        if (chosenKey == AdHocGroupName)
        {
            var single = chosen.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).First();
            chosen = new List<IssueRecord> { single };
            name = single.Title;
            goal = single.Description is { Length: > 500 } d ? d[..500] : single.Description
                ?? $"Complete {single.Id}: {single.Title}";
        }
        else if (chosenKey.StartsWith(FollowUpThemePrefix, StringComparison.Ordinal)
            && chosen.Count > MaxThemeTasks)
        {
            chosen = chosen.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt)
                .Take(MaxThemeTasks).ToList();
        }

        var start = DateTime.UtcNow;
        var sprint = await sprints.CreateAsync(new NewSprint(
            Name: name,
            Goal: goal,
            StartDate: start,
            EndDate: start.Add(SprintDuration),
            Status: SprintStatus.Active), ct);

        // Link tasks AND their parent stories (stories power the
        // Sprints page progress display; they don't gate completion).
        var toLink = new HashSet<string>(chosen.Select(t => t.Id), StringComparer.Ordinal);
        foreach (var task in chosen)
        {
            if (task.ParentIssueId is not null)
            {
                toLink.Add(task.ParentIssueId);
            }
        }
        foreach (var id in toLink)
        {
            await sprints.AddIssueAsync(sprint.Id, id, ct);
        }

        // Re-publish the activation hint AFTER membership links commit:
        // the store-level CreateAsync(Active) publish races the linking
        // writes, so a dispatch loop woken by it can see an active
        // sprint with an empty member list and park with no further
        // hint coming (observed in the e2e smoke). Hints are cheap and
        // idempotent — this second one is the reliable kick.
        if (_eventPublisher is not null)
        {
            var activatedAt = DateTimeOffset.UtcNow;
            await _eventPublisher.PublishAsync(new Core.Messaging.SprintStatusChanged
            {
                MessageId = Core.Messaging.SprintStatusChanged.IdFor(projectId, sprint.Id, "Active:linked", activatedAt),
                ProjectId = projectId,
                SprintId = sprint.Id,
                FromStatus = "(new)",
                ToStatus = "Active",
                ChangedAt = activatedAt,
            }, ct);
        }

        _logger.LogInformation(
            "Sprint {SprintId} assembled + activated: '{Name}' with {Tasks} task(s) (group={Group})",
            sprint.Id, name, chosen.Count, chosenKey);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintStarted,
            null, $"Sprint '{name}' started with {chosen.Count} task(s)",
            new Dictionary<string, object?> { ["sprintId"] = sprint.Id, ["taskCount"] = chosen.Count }));
        return (sprint.Id, name, chosen.Count);
    }

    /// <summary>
    /// Walk the parent chain (task -> story -> spec) to the group key.
    /// Stories parent to the spec ID, which is not an issue row, so
    /// the walk stops there and the spec id becomes the group key
    /// (one group per groomed spec — the correct granularity: a
    /// groomed spec IS the theme). Tasks whose chain ends inside the
    /// issue table (or has no parent) get the ad-hoc group key.
    /// </summary>
    internal static string ResolveGroupKey(IssueRecord task, IReadOnlyDictionary<string, IssueRecord> byId)
    {
        var current = task;
        var hops = 0;
        while (current.ParentIssueId is not null && hops < 10)
        {
            if (!byId.TryGetValue(current.ParentIssueId, out var parent))
            {
                // Parent lives outside the issue table (a spec row):
                // use it as the stable group key.
                return current.ParentIssueId;
            }
            current = parent;
            hops++;
        }
        return AdHocGroupName;
    }

    /// <summary>Prefix for follow-up theme groups (see <see cref="ResolveThemeKey"/>).</summary>
    public const string FollowUpThemePrefix = "followup:";

    /// <summary>Max tasks packed into one sprint from a single follow-up theme; the remainder stays eligible for the next sprint.</summary>
    internal const int MaxThemeTasks = 10;

    /// <summary>
    /// The ASSEMBLY theme key (operator rule 2026-08-08: there is
    /// exactly one kind of sprint — a themed bundle; follow-up work
    /// NEVER spawns a solo sprint). Spec-chained tasks group under
    /// their spec (<see cref="ResolveGroupKey"/>) — a groomed spec IS
    /// the theme. Parentless tasks with a <c>followUpOf</c> chain group
    /// under the chain's ROOT ancestor (<c>followup:&lt;rootId&gt;</c>):
    /// follow-ups filed from the same work are definitionally the same
    /// theme and pack into ONE sprint together. Only truly rootless
    /// ad-hoc tasks (operator-enqueued, no chain) keep the singleton
    /// AdHocGroupName path — they are the genuinely unrelated work the
    /// old solo-sprint rule (2026-07-27) was written for.
    /// </summary>
    internal static string ResolveThemeKey(IssueRecord task, IReadOnlyDictionary<string, IssueRecord> byId)
    {
        var specKey = ResolveGroupKey(task, byId);
        if (specKey != AdHocGroupName) return specKey;

        var seen = new HashSet<string>(StringComparer.Ordinal) { task.Id };
        string? root = null;
        var current = task;
        var hops = 0;
        while (hops++ < 20 && current.GetMetadata("followUpOf") is { } fu)
        {
            var next = fu.Split(',')[0].Trim();
            if (next.Length == 0 || !seen.Add(next)) break;
            root = next;
            if (!byId.TryGetValue(next, out var parent)) break;
            current = parent;
        }
        return root is null ? AdHocGroupName : FollowUpThemePrefix + root;
    }

    /// <summary>
    /// Cross-project guard: a spec group belongs to the project that
    /// OWNS the spec. Tasks physically present in this project's
    /// store but chained to another project's spec are a routing bug
    /// (observed live 2026-07-29: porthorizon stories groomed into
    /// the forge store were assembled and dispatched against the
    /// Forge repo — bogus PRs #66/#67). Never assemble them here;
    /// log loudly so the operator sees the violation. Returns the
    /// number of groups dropped.
    /// </summary>
    internal static async Task<int> DropCrossProjectGroupsAsync(
        Dictionary<string, List<IssueRecord>> groups,
        List<string> groupOrder,
        string projectId,
        ISpecStore specs,
        ILogger logger,
        CancellationToken ct)
    {
        var dropped = 0;
        foreach (var key in groupOrder.ToList())
        {
            if (key == AdHocGroupName || key.StartsWith(FollowUpThemePrefix, StringComparison.Ordinal)) continue;
            var groupSpec = await specs.GetAsync(key, ct);
            if (groupSpec is not null
                && !string.Equals(groupSpec.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "SprintAssembler: {Count} task(s) in project {Project}'s store chain to spec {SpecId} owned by project {SpecProject} — skipping (routing violation)",
                    groups[key].Count, projectId, key, groupSpec.ProjectId);
                groups.Remove(key);
                groupOrder.Remove(key);
                dropped++;
            }
        }
        return dropped;
    }
}
