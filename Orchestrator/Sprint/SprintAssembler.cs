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
/// Assembly is deterministic: eligible tasks are grouped by their
/// root epic (task → story → spec → epic via parent_issue_id) and
/// the oldest epic's group becomes the next sprint (name = epic
/// title, goal = epic description). Parentless tasks fall into an
/// "Ad-hoc work" group. Stories are linked too (progress display);
/// completion counts non-container tasks only.
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
        Core.IFollowUpTriage? followUpTriage = null)
    {
        _projects = projects;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _gates = gates;
        _followUpTriage = followUpTriage;
    }

    private readonly Core.IFollowUpTriage? _followUpTriage;

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
        int ActiveTerminal);

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

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await TickAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
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
                await InjectAdHocAsync(active, issues, sprints, ct);
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
            await WriteBuildStateAsync(issues, EmptyState(
                "idle", "No eligible work in the backlog — nothing to assemble",
                completedSprintId, completedSprintName), ct);
        }
    }

    /// <summary>
    /// Completion progress: every linked non-container issue counts;
    /// stories linked for progress display don't gate completion; an
    /// empty sprint (defensive) is complete.
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
            if (issue.Status is IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Closed)
            {
                terminal++;
            }
        }
        return (terminal == total, total, terminal);
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
                var key = ResolveGroupKey(task, byId);
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
                var name = key == AdHocGroupName
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
        SprintRecord active, IIssueStore issues, ISprintStore sprints, CancellationToken ct)
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

        // Group by root epic (walk task -> story -> spec -> epic).
        // The chain crosses tables at the spec (a story's parent is
        // the spec ID, not an issue row), so the walk returns the
        // spec id as the group key — one group per groomed spec,
        // which IS the theme. Parentless tasks get the ad-hoc key.
        var groups = new Dictionary<string, List<IssueRecord>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        foreach (var task in eligible)
        {
            var groupKey = ResolveGroupKey(task, byId);
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

        // Ad-hoc assembly is ALWAYS a solo sprint (highest priority
        // first, then oldest) — never a bundle.
        if (chosenKey == AdHocGroupName)
        {
            var single = chosen.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).First();
            chosen = new List<IssueRecord> { single };
            name = single.Title;
            goal = single.Description is { Length: > 500 } d ? d[..500] : single.Description
                ?? $"Complete {single.Id}: {single.Title}";
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
            if (key == AdHocGroupName) continue;
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
