using Forge.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Messaging;

/// <summary>
/// 15-minute backstop tick publisher. Hint events drive the fast path;
/// these ticks are the safety net that re-derives everything from
/// DB/GitHub truth if hints are lost (crash, machine suspend, consumer
/// fault). Publishes one <see cref="SweepTick"/> per kind per registered
/// project every interval.
/// </summary>
public sealed class SweepTickPublisher : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IEventPublisher _publisher;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _projectIds;
    private readonly ILogger<SweepTickPublisher> _logger;

    public SweepTickPublisher(
        IEventPublisher publisher,
        Func<CancellationToken, Task<IReadOnlyList<string>>> projectIds,
        ILogger<SweepTickPublisher> logger)
    {
        _publisher = publisher;
        _projectIds = projectIds;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One round immediately at startup: the pre-messaging dispatch
        // loop swept watches on its FIRST cycle, and in-flight work
        // (merge-ready PRs, pending verdicts) produces no fresh hints
        // after a restart — waiting a full interval for the first tick
        // regresses takeover by up to 15 minutes.
        await PublishTicksAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PublishTicksAsync(stoppingToken);
        }
    }

    private async Task PublishTicksAsync(CancellationToken ct)
    {
        var tickAt = DateTimeOffset.UtcNow;
        IReadOnlyList<string> projects;
        try
        {
            projects = await _projectIds(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SweepTick: project enumeration failed; skipping tick");
            return;
        }

        foreach (var projectId in projects)
        {
            foreach (var kind in Enum.GetValues<SweepKind>())
            {
                await _publisher.PublishAsync(new SweepTick
                {
                    MessageId = Core.Messaging.SweepTick.IdFor(kind, projectId, tickAt),
                    ProjectId = projectId,
                    Kind = kind,
                    TickAt = tickAt,
                }, ct);
            }
        }
        _logger.LogInformation("SweepTick published for {Count} project(s) at {TickAt:O}", projects.Count, tickAt);
    }
}
