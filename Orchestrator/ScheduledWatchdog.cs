using Forge.Core;
using Forge.Dashboard;
using Forge.Projects;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator;

/// <summary>
/// The watchdog scheduler (operator-approved v1, 2026-07-31): scans
/// every registered project for structural stalls on a slow tick and
/// reconciles the finding store (dedupe + auto-resolve). Alert-only —
/// mechanisms fix, the watchdog sees. New findings publish to the
/// dashboard event bus; the Now attention feed reads open findings.
/// </summary>
public sealed class ScheduledWatchdog
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly ProjectContextFactory _projects;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<ScheduledWatchdog> _logger;
    private readonly Core.TaskStateMachine? _lifecycle;

    public ScheduledWatchdog(
        ProjectContextFactory projects,
        IDashboardEventBus events,
        ILogger<ScheduledWatchdog> logger,
        Core.TaskStateMachine? lifecycle = null)
    {
        _projects = projects;
        _events = events;
        _logger = logger;
        _lifecycle = lifecycle;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger: don't fight startup recovery or the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
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

    /// <summary>One scan pass over all projects. Exposed for tests.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        foreach (var project in _projects.KnownProjects)
        {
            if (ct.IsCancellationRequested) return;
            var ctx = _projects.Find(project.Id);
            if (ctx is null) continue;
            try
            {
                await TickProjectAsync(project.Id, ctx.Issues, ctx.Sprints, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ScheduledWatchdog: tick failed for project {ProjectId}; continuing", project.Id);
            }
        }
    }

    internal async Task TickProjectAsync(string projectId, IIssueStore issues, ISprintStore sprints, CancellationToken ct)
    {
        var findings = await WatchdogScanner.ScanAsync(issues, sprints, DateTime.UtcNow, ct);
        var store = new WatchdogFindingStore((IssueStore)issues);
        var result = await store.SyncAsync(findings, DateTime.UtcNow, ct);
        foreach (var f in result.NewFindings)
        {
            _logger.LogWarning("Watchdog ({Project}): {Kind} — {Detail}", projectId, f.Kind, f.Detail);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.WatchdogFinding,
                f.TargetId, $"watchdog/{f.Kind}: {f.Detail}",
                new Dictionary<string, object?> { ["projectId"] = projectId, ["severity"] = f.Severity }));
        }
        if (result.Added + result.Updated + result.Resolved > 0)
        {
            _logger.LogInformation(
                "Watchdog ({Project}): {Added} new, {Updated} ongoing, {Resolved} resolved",
                projectId, result.Added, result.Updated, result.Resolved);
        }

        // Auto-remediation (operator-approved 2026-07-31): orphaned
        // claims — InProgress with an assignee but no active run —
        // requeue in-process instead of waiting for a restart. The
        // shared recovery_attempts budget (3) caps it; beyond that
        // the starvation finding above is the operator's signal.
        //
        // Zombie run rows FIRST: a dead run whose agent_run row was
        // never closed (restart, timeout) counts as "active" to the
        // reaper and would shield its orphaned claim forever
        // (observed live 2026-08-01: 4 reviewer rows, up to 9h old,
        // masking their tasks). 20-minute heartbeat staleness: the
        // runner heartbeats after every model response; the longest
        // legitimate silence is a slow single round-trip.
        var runStore = new Core.AgentRunStore(((Core.IssueStore)issues).Db);
        var zombies = await runStore.FailZombieRunsAsync(
            DateTime.UtcNow - TimeSpan.FromMinutes(20),
            "zombie run reaped: heartbeat stale > 20m (process died or run wedged)", ct);
        foreach (var id in zombies)
        {
            _logger.LogWarning("Watchdog ({Project}): closed zombie agent run {RunId}", projectId, id);
        }

        var reaped = await Core.OrphanedClaimReaper.ReapAsync(
            issues, runStore, DateTime.UtcNow, ct: ct);
        foreach (var id in reaped)
        {
            _logger.LogWarning("Watchdog ({Project}): requeued orphaned claim {TaskId}", projectId, id);
            // A reaped task whose recorded state is terminal (Failed/
            // BlockedOperator — reclaimed after a terminal transition
            // cleared its bookkeeping) must be coerced back through
            // the machine, or the next dispatch violates and the
            // state stays wrong for the round (task-18, 2026-08-01).
            if (_lifecycle is not null)
            {
                var fresh = await issues.GetAsync(id, ct);
                if (fresh?.GetMetadata("state") is nameof(Core.TaskLifecycleState.Failed)
                    or nameof(Core.TaskLifecycleState.BlockedOperator))
                {
                    await _lifecycle.ReportAsync(issues, fresh!, Core.TaskEvent.OperatorRequeue,
                        watch: null, hasActiveDevRun: false, ct);
                }
            }
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
                id, $"watchdog requeued orphaned claim (project={projectId})",
                new Dictionary<string, object?> { ["projectId"] = projectId }));
        }
    }
}
