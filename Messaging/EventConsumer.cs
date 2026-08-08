using Forge.Core.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Messaging;

/// <summary>
/// BackgroundService base for event consumers: consume → handle → Commit.
/// Handler faults Nack (redelivery / DLQ routing) and are logged, never
/// swallowed — MAF InProcessExecution-style silent fault swallowing is
/// the PRWatcher lesson. Handlers must be idempotent (redelivery after a
/// pre-Commit crash is expected) and must not block on long work — they
/// kick, the existing run registry owns the long work.
/// Supervision: a fault OUTSIDE the per-message handler (consumer
/// creation, transport stream error, a cleanly-completed stream) restarts
/// the session with bounded backoff until the host stops the service —
/// a dead consumer must never silently take its topic's fast path (and,
/// for <c>SweepTick</c>, every 15-minute backstop) down with it.
/// </summary>
public abstract class EventConsumer<T> : BackgroundService where T : IForgeEvent
{
    private static readonly TimeSpan MaxRestartBackoff = TimeSpan.FromMinutes(2);

    private readonly ITransport _transport;
    private readonly ILogger _logger;

    protected EventConsumer(ITransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>Restart delay after the first session fault; doubles
    /// up to 2 minutes while faults are rapid-fire. Exposed for tests.</summary>
    protected virtual TimeSpan InitialBackoff => TimeSpan.FromSeconds(5);

    protected virtual string Topic => Topics.For<T>();

    protected abstract Task HandleAsync(T evt, CancellationToken ct);

    /// <summary>Last successful handle, for watchdog liveness assertions.</summary>
    public DateTimeOffset? LastHandledAtUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sessionStartedAt = DateTimeOffset.UtcNow;
            try
            {
                await ConsumeLoopAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested) return;
                // The transport closed the stream cleanly. With no
                // cancellation that still means the consumer is gone —
                // recreate it rather than silently abandoning the topic.
                _logger.LogWarning("Event consumer stream completed: {Consumer} on {Topic} — recreating", GetType().Name, Topic);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Event consumer faulted: {Consumer} on {Topic} — restarting in {Backoff}",
                    GetType().Name, Topic, backoff);
            }

            // A session that stayed healthy past the max backoff earns
            // a fresh budget; only rapid-fire faults escalate.
            if (DateTimeOffset.UtcNow - sessionStartedAt > MaxRestartBackoff)
                backoff = InitialBackoff;

            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxRestartBackoff.Ticks));
        }
    }

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        await using var consumer = await _transport.CreateConsumerAsync<T>(
            Topic, new ConsumerOptions(), stoppingToken);

        _logger.LogInformation("Event consumer started: {Consumer} on {Topic}", GetType().Name, Topic);

        await foreach (var envelope in consumer.ConsumeAsync(stoppingToken))
        {
            try
            {
                await HandleAsync(envelope.Payload, stoppingToken);
                await consumer.CommitAsync(envelope, stoppingToken);
                LastHandledAtUtc = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler faulted: {Consumer} message {MessageId} — Nack (redelivery/DLQ)",
                    GetType().Name, envelope.Headers.MessageId);
                try
                {
                    await consumer.NackAsync(envelope, CancellationToken.None);
                }
                catch (Exception nackEx)
                {
                    _logger.LogError(nackEx, "Nack failed for {MessageId} on {Topic}", envelope.Headers.MessageId, Topic);
                }
            }
        }
    }
}
