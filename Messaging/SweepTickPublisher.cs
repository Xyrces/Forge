using Forge.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Messaging;

/// <summary>
/// Polls GitHub watch truth every minute; planning keeps its 15-minute
/// backstop. All kinds run immediately at startup. Events remain hints:
/// consumers re-read truth and retain their existing serialization and gates.
/// </summary>
public sealed class SweepTickPublisher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan WatchInterval = TimeSpan.FromMinutes(1);

    private readonly IEventPublisher _publisher;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _projectIds;
    private readonly ILogger<SweepTickPublisher> _logger;
    private readonly TimeProvider _time;

    public SweepTickPublisher(
        IEventPublisher publisher,
        Func<CancellationToken, Task<IReadOnlyList<string>>> projectIds,
        ILogger<SweepTickPublisher> logger,
        TimeProvider? timeProvider = null)
    {
        _publisher = publisher;
        _projectIds = projectIds;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastBackstop = _time.GetTimestamp();
        // PeriodicTimer coalesces missed ticks rather than replaying a backlog
        // after a slow publication or suspended host. Only one producer loop runs.
        using var timer = new PeriodicTimer(WatchInterval, _time);
        await PublishTicksAsync(allKinds: true, stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var allKinds = _time.GetElapsedTime(lastBackstop) >= Interval;
            if (allKinds) lastBackstop = _time.GetTimestamp();
            await PublishTicksAsync(allKinds, stoppingToken);
        }
    }

    private async Task PublishTicksAsync(bool allKinds, CancellationToken ct)
    {
        var tickAt = _time.GetUtcNow();
        IReadOnlyList<string> projects;
        try
        {
            projects = await _projectIds(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SweepTick: project enumeration failed; skipping tick");
            return;
        }

        var kinds = allKinds ? Enum.GetValues<SweepKind>() : [SweepKind.Watch];
        foreach (var projectId in projects)
        {
            foreach (var kind in kinds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _publisher.PublishAsync(new SweepTick
                    {
                        MessageId = Core.Messaging.SweepTick.IdFor(kind, projectId, tickAt),
                        ProjectId = projectId,
                        Kind = kind,
                        TickAt = tickAt,
                    }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Production publishers already log transport failures. Keep
                    // this scheduler alive if another implementation throws.
                    _logger.LogError(ex, "SweepTick publication failed for {Project}/{Kind}; continuing", projectId, kind);
                }
            }
        }
        _logger.LogInformation("SweepTick published for {Count} project(s) at {TickAt:O} (all kinds: {AllKinds})", projects.Count, tickAt, allKinds);
    }
}
