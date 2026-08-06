using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator;

/// <summary>
/// P3.5: scheduled Groomer. Wakes up every <c>Interval</c> and
/// dispatches the GroomerAgent against any Approved specs that
/// haven't been groomed recently (or whose last groom failed).
/// Each run writes a row to <c>issue_groomer_run</c> with
/// trigger=<c>scheduled</c>.
///
/// <para>
/// Manual grooming via <c>POST /api/specs/{id}/groom</c> writes
/// the same table with trigger=<c>manual</c>. The scheduler is
/// best-effort: it catches exceptions per-spec and continues; a
/// failing spec doesn't block the others.
/// </para>
///
/// <para>
/// Designed as a plain class (not a <c>BackgroundService</c>)
/// because the orchestrator doesn't use an
/// <see cref="Microsoft.Extensions.Hosting.IHost"/>. The
/// composition root in <c>Program.cs</c> launches it as a
/// fire-and-forget task tied to the shutdown token.
/// </para>
/// </summary>
public sealed class ScheduledGroomer
{
    private readonly ISpecStore _specs;
    private readonly GroomerAgentFactory _groomerFactory;
    private readonly IssueGroomerRunStore _runStore;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<ScheduledGroomer> _logger;
    private readonly TimeSpan _interval;
    private readonly IIssueStore? _issues;
    private readonly ISprintStore? _sprints;
    private readonly StageGates? _gates;
    private readonly Projects.ProjectContextFactory? _projectContexts;

    /// <summary>Max ad-hoc tasks groomed per tick (LLM cost bound).</summary>
    internal const int MaxTaskGroomsPerTick = 3;

    public ScheduledGroomer(
        ISpecStore specs,
        GroomerAgentFactory groomerFactory,
        IssueGroomerRunStore runStore,
        IDashboardEventBus events,
        ILogger<ScheduledGroomer> logger,
        TimeSpan? interval = null,
        IIssueStore? issues = null,
        ISprintStore? sprints = null,
        StageGates? gates = null,
        // Multi-project: when set, the ad-hoc pass sweeps EVERY
        // registered project's store (each groomed against its own
        // queue) instead of only the primary store. Spec-driven
        // grooming routes via the factory regardless.
        Projects.ProjectContextFactory? projectContexts = null)
    {
        _specs = specs;
        _groomerFactory = groomerFactory;
        _runStore = runStore;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _issues = issues;
        _sprints = sprints;
        _gates = gates;
        _projectContexts = projectContexts;
    }

    public TimeSpan Interval => _interval;

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger the first tick so we don't fight the dashboard at boot.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
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
        if (_gates is not null && await _gates.IsHeldAsync(StageGates.Groom, ct))
        {
            _logger.LogInformation("ScheduledGroomer: held by operator gate; skipping tick");
            return;
        }

        IReadOnlyList<SpecRecord> candidates;
        try
        {
            // P2.a / P2.b: pull all specs and filter in C#. The
            // candidate set is the "ready to groom" statuses:
            // Designed (Designer approved), AssetReady, Approved
            // (operator fast-path). Groomed is deliberately NOT
            // auto-groomed: grooming appends stories/tasks, so a
            // terminal spec in the candidate set gets re-decomposed
            // every interval forever (observed 2026-07-22: ~27 runs
            // → 83 stories / 147 tasks / 28 PRs for one spec).
            // Intentional re-decomposition goes through the manual
            // endpoint with ?force=true.
            var all = await _specs.ListAsync(projectId: null, status: null, ct);
            candidates = all.Where(s => s.Status is SpecStatus.Designed
                or SpecStatus.AssetReady
                or SpecStatus.Approved).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduledGroomer: list specs failed; skipping tick");
            return;
        }

        foreach (var spec in candidates)
        {
            if (!await ShouldGroomAsync(spec, ct)) continue;
            try
            {
                await RunOneAsync(spec, GroomerTriggerKind.Scheduled, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ScheduledGroomer: spec {Id} failed; continuing", spec.Id);
            }
        }

        await GroomAdHocTasksAsync(ct);
    }

    /// <summary>
    /// Technical grooming for ad-hoc tasks (operator-enqueued or
    /// agent-filed follow-ups). The sprint assembler refuses ad-hoc
    /// tasks until this pass marks them groomed — no task enters a
    /// sprint without grooming. Bounded at
    /// <see cref="MaxTaskGroomsPerTick"/> per tick (LLM cost);
    /// leftovers are picked up next tick, oldest first.
    /// </summary>
    private async Task GroomAdHocTasksAsync(CancellationToken ct)
    {
        // Multi-project: sweep each registered project's own queue.
        // Without this the ad-hoc pass only ever saw the PRIMARY
        // store, so a second project's ad-hoc tasks never became
        // sprint-eligible.
        if (_projectContexts is not null)
        {
            foreach (var project in _projectContexts.KnownProjects)
            {
                var ctx = _projectContexts.Find(project.Id);
                if (ctx is null) continue;
                try
                {
                    await GroomAdHocTasksForStoreAsync(project.Id, ctx.Issues, ctx.Sprints, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ScheduledGroomer: ad-hoc sweep failed for project {ProjectId}; continuing", project.Id);
                }
            }
            return;
        }
        if (_issues is null) return;
        await GroomAdHocTasksForStoreAsync(projectId: null, _issues, _sprints, ct);
    }

    private async Task GroomAdHocTasksForStoreAsync(string? projectId, IIssueStore issues, ISprintStore? sprints, CancellationToken ct)
    {
        List<IssueRecord> pending;
        try
        {
            pending = (await issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduledGroomer: list issues failed; skipping ad-hoc sweep");
            return;
        }

        var byId = (await issues.ListAsync(new IssueFilter(), ct)).ToDictionary(i => i.Id);

        var ungroomedAll = pending
            .Where(i => !AgentTaskTypes.IsContainer(i.Type)
                && i.Type != AgentTaskTypes.PrWatch
                // Sprint membership does NOT exempt an ad-hoc task:
                // blocksIssueId blockers are now BORN into the active
                // sprint ungroomed (FollowUpTool 2026-07-31), and the
                // dispatch loop refuses ungroomed parentless members —
                // if this sweep also skipped sprinted tasks, those
                // blockers would deadlock the sprint (observed live
                // 2026-08-01: task-365/366 wedged 6h, flagged by the
                // groomer-wedge watchdog check). Spec-CHAIN members
                // are still excluded — by the AdHocGroupName test:
                // they have a parent chain, ad-hoc tasks don't.
                && Sprint.SprintAssembler.ResolveGroupKey(i, byId) == Sprint.SprintAssembler.AdHocGroupName
                && !string.Equals(i.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.CreatedAt)
            .ToList();
        var totalUngroomed = ungroomedAll.Count;
        var ungroomed = ungroomedAll.Take(MaxTaskGroomsPerTick).ToList();

        var processed = 0;
        foreach (var task in ungroomed)
        {
            processed++;
            string? outcome;
            try
            {
                var groomer = _groomerFactory.Create(projectId: projectId);
                outcome = await groomer.GroomTaskAsync(task.Id, ct);
                _logger.LogInformation("ScheduledGroomer: ad-hoc task {Id} groom outcome={Outcome}", task.Id, outcome ?? "no-decision");
            }
            catch (Exception ex)
            {
                outcome = "failed";
                _logger.LogWarning(ex, "ScheduledGroomer: ad-hoc task {Id} groom failed; continuing", task.Id);
            }
            // Build-state visibility (operator request 2026-08-06):
            // one event per groomed task so the Sprints page shows
            // the grooming countdown draining in real time instead of
            // a sprint that appears stuck between ticks.
            _events.Publish(new Dashboard.DashboardEvent(DateTime.UtcNow, Dashboard.DashboardEventKind.GroomerAdHocCompleted,
                task.Id, $"Ad-hoc groom {outcome ?? "no-decision"}: {task.Title}",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["outcome"] = outcome ?? "no-decision",
                    ["remaining"] = Math.Max(0, totalUngroomed - processed),
                }));
        }
    }

    /// <summary>
    /// Heuristic: groom a spec at most every <c>Interval</c>, or
    /// immediately if the last run failed. Cheap query: <c>SELECT
    /// ts FROM issue_groomer_run WHERE spec_id = $id ORDER BY ts
    /// DESC LIMIT 1</c>.
    /// </summary>
    private async Task<bool> ShouldGroomAsync(SpecRecord spec, CancellationToken ct)
    {
        var recent = await _runStore.ListAsync(specId: spec.Id, limit: 1, ct);
        if (recent.Count == 0) return true;
        var last = recent[0];
        if (last.Status == GroomerRunStatus.Failed) return true;
        return DateTime.UtcNow - last.Ts >= _interval;
    }

    private async Task RunOneAsync(SpecRecord spec, GroomerTriggerKind trigger, CancellationToken ct)
    {
        var run = await _runStore.StartAsync(spec.Id, trigger, ct);
        var startedAt = DateTime.UtcNow;
        _logger.LogInformation("ScheduledGroomer: starting run {RunId} for spec {SpecId} (trigger={Trigger})",
            run.Id, spec.Id, trigger);

        try
        {
            var groomer = _groomerFactory.Create(projectId: spec.ProjectId);
            var result = await groomer.GroomAsync(spec.Id, ct);
            var duration = DateTime.UtcNow - startedAt;
            await _runStore.FinishAsync(
                run.Id, GroomerRunStatus.Succeeded,
                storiesProduced: result?.StoryIds.Count ?? 0,
                tasksProduced: result?.TaskIds.Count ?? 0,
                error: null,
                duration: duration,
                ct: ct);
            _logger.LogInformation("ScheduledGroomer: spec {SpecId} groomed in {Ms}ms ({Stories} stories, {Tasks} tasks)",
                spec.Id, duration.TotalMilliseconds, result?.StoryIds.Count ?? 0, result?.TaskIds.Count ?? 0);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startedAt;
            await _runStore.FinishAsync(
                run.Id, GroomerRunStatus.Failed,
                storiesProduced: 0, tasksProduced: 0,
                error: $"{ex.GetType().Name}: {ex.Message}",
                duration: duration,
                ct: ct);
            _logger.LogWarning(ex, "ScheduledGroomer: spec {SpecId} failed in {Ms}ms", spec.Id, duration.TotalMilliseconds);
        }
    }
}