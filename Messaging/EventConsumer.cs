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
/// </summary>
public abstract class EventConsumer<T> : BackgroundService where T : IForgeEvent
{
    private readonly ITransport _transport;
    private readonly ILogger _logger;

    protected EventConsumer(ITransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    protected virtual string Topic => Topics.For<T>();

    protected abstract Task HandleAsync(T evt, CancellationToken ct);

    /// <summary>Last successful handle, for watchdog liveness assertions.</summary>
    public DateTimeOffset? LastHandledAtUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
