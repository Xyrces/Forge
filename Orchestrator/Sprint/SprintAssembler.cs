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
/// Operator-created ad-hoc tasks are eligible too — ALL work is
/// sprint work, even a single item.</item>
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

    public const string AdHocGroupName = "Ad-hoc work";
    private static readonly TimeSpan SprintDuration = TimeSpan.FromDays(14);

    public SprintAssembler(
        ProjectContextFactory projects,
        IDashboardEventBus events,
        ILogger<SprintAssembler> logger,
        TimeSpan? interval = null)
    {
        _projects = projects;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
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
            if (!await IsCompleteAsync(active, issues, sprints, ct)) return;
            await sprints.UpdateAsync(active.Id,
                new Dictionary<string, object?> { ["status"] = nameof(SprintStatus.Completed) }, ct);
            _logger.LogInformation("Sprint {SprintId} ({Name}) completed — all member tasks terminal (project={Project})",
                active.Id, active.Name, projectId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.SprintCompleted,
                null, $"Sprint '{active.Name}' completed"));
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

    private async Task AssembleNextAsync(string projectId, IIssueStore issues, ISprintStore sprints, ISpecStore specs, CancellationToken ct)
    {
        var all = await issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct);
        var byId = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);

        // Eligible: Pending + not a container + not a watch + not
        // already linked to any sprint (Active/Completed both count —
        // re-ingesting history would resurrect finished work).
        var sprinted = new HashSet<string>();
        foreach (var s in await sprints.ListAsync(activeOnly: false, ct))
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
            return (spec?.Title ?? key,
                epicDesc ?? $"Complete all groomed tasks for {spec?.Title ?? key}.",
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
}
