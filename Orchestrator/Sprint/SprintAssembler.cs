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
        StageGates? gates = null)
    {
        _projects = projects;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _gates = gates;
    }

    public TimeSpan Interval => _interval;

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
        var active = await sprints.GetActiveAsync(ct);
        if (active is not null)
        {
            if (!await IsCompleteAsync(active, issues, sprints, ct))
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
                return;
            }
            await sprints.UpdateAsync(active.Id,
                new Dictionary<string, object?> { ["status"] = nameof(SprintStatus.Completed) }, ct);
            _logger.LogInformation("Sprint {SprintId} ({Name}) completed — all member tasks terminal (project={Project})",
                active.Id, active.Name, projectId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintCompleted,
                null, $"Sprint '{active.Name}' completed"));
        }

        // Epic lifecycle: close epics whose entire tree is terminal
        // (they otherwise linger as Pending on the board forever).
        await CloseTerminalEpicsAsync(issues, specs, ct);

        // Operator gate: completing a finished sprint is bookkeeping
        // (always allowed); STARTING new work is the gated decision.
        if (_gates is not null && await _gates.IsHeldAsync(StageGates.Sprint, ct))
        {
            _logger.LogInformation("Sprint assembly held by operator gate (project={Project})", projectId);
            return;
        }

        await AssembleNextAsync(projectId, issues, sprints, specs, ct);
    }

    /// <summary>
    /// A sprint is complete when every linked non-container issue is
    /// terminal. Stories linked for progress display don't gate
    /// completion; an empty sprint (defensive) is complete.
    /// </summary>
    private static async Task<bool> IsCompleteAsync(SprintRecord sprint, IIssueStore issues, ISprintStore sprints, CancellationToken ct)
    {
        var memberIds = await sprints.GetIssueIdsAsync(sprint.Id, ct);
        foreach (var id in memberIds)
        {
            var issue = await issues.GetAsync(id, ct);
            if (issue is null || AgentTaskTypes.IsContainer(issue.Type)
                || issue.Type == AgentTaskTypes.PrWatch)
            {
                continue;
            }
            if (issue.Status is not (IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Closed))
            {
                return false;
            }
        }
        return true;
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
        // Same work: walk the followUpOf chain; a hit on any sprint
        // member means this task is a continuation of the sprint's
        // own work.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = task.GetMetadata("followUpOf");
        while (cur is not null && seen.Add(cur))
        {
            if (memberIds.Contains(cur))
            {
                return $"same work (follow-up of {cur})";
            }
            cur = all.TryGetValue(cur, out var parent) ? parent.GetMetadata("followUpOf") : null;
        }
        // Unblocks: a blocks edge has this task blocking a sprint
        // member — the member cannot proceed until it lands.
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

    private async Task AssembleNextAsync(string projectId, IIssueStore issues, ISprintStore sprints, ISpecStore specs, CancellationToken ct)
    {
        var all = await issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct);
        var byId = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);

        // Eligible: Pending + not a container + not a watch + not
        // already linked to an ACTIVE sprint (never double-stage work
        // that's already on a live board). Completed-sprint membership
        // is NOT disqualifying: a sprint only completes when every
        // member is terminal, so a Pending task whose sole membership
        // is a Completed sprint is definitionally an operator requeue
        // (e.g. /api/tasks/{id}/requeue from Failed) — excluding it
        // would strand requeued work forever (observed live
        // 2026-07-24: task-158 requeued after its sprint completed
        // and never re-assembled).
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

        // Eligible for ASSEMBLY: spec-chain groups, plus groomed
        // ad-hoc tasks as SOLO sprints (operator rules 2026-07-27):
        // related/unblocking ad-hoc work INJECTS into the active
        // sprint instead of waiting; unrelated groomed ad-hoc work
        // gets its own focused one-task sprint — coherent and
        // deployable with zero cross-task side effects. What never
        // returns: bundling multiple unrelated ad-hoc tasks into
        // one sprint (the grab-bag problem).
        eligible = eligible
            .Where(t => ResolveGroupKey(t, byId) != AdHocGroupName
                || string.Equals(t.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (eligible.Count == 0) return;

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
        if (groupOrder.Count == 0) return;

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
        var chosenKey = groupOrder
            .OrderBy(k => described[k].CreatedAt)
            .ThenBy(k => k == AdHocGroupName ? 1 : 0)
            .First();
        var chosen = groups[chosenKey];
        var (name, goal, _) = described[chosenKey];

        // Ad-hoc assembly is ALWAYS a solo sprint (oldest first) —
        // never a bundle. Related ad-hoc work would have injected
        // into the active sprint instead of reaching here.
        if (chosenKey == AdHocGroupName)
        {
            var single = chosen.OrderBy(t => t.CreatedAt).First();
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
