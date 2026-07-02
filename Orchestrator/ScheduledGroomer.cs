using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;

namespace PortHorizon.Agents.Orchestrator;

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

    public ScheduledGroomer(
        ISpecStore specs,
        GroomerAgentFactory groomerFactory,
        IssueGroomerRunStore runStore,
        IDashboardEventBus events,
        ILogger<ScheduledGroomer> logger,
        TimeSpan? interval = null)
    {
        _specs = specs;
        _groomerFactory = groomerFactory;
        _runStore = runStore;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
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
        IReadOnlyList<SpecRecord> approved;
        try
        {
            // ISpecStore.ListAsync filters by status; pass Approved.
            approved = await _specs.ListAsync(projectId: null, status: SpecStatus.Approved, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduledGroomer: list specs failed; skipping tick");
            return;
        }

        foreach (var spec in approved)
        {
            if (spec.Status != SpecStatus.Approved) continue;
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
            var groomer = _groomerFactory.Create();
            await groomer.GroomAsync(spec.Id, ct);
            var duration = DateTime.UtcNow - startedAt;
            // We don't have a clean way to count stories + tasks
            // produced by the groomer from here; the GroomerAgent
            // itself doesn't return them. The dashboard's Groomer
            // timeline shows the started + succeeded/failed pair,
            // and the spec's child-issue count can be read off the
            // spec page.
            await _runStore.FinishAsync(
                run.Id, GroomerRunStatus.Succeeded,
                storiesProduced: 0, tasksProduced: 0,
                error: null,
                duration: duration,
                ct: ct);
            _logger.LogInformation("ScheduledGroomer: spec {SpecId} groomed in {Ms}ms", spec.Id, duration.TotalMilliseconds);
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