using System.Collections.Concurrent;
using Forge.Core.Messaging;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Messaging;

/// <summary>
/// Talaria-backed <see cref="IEventPublisher"/>. Topic-per-event-type,
/// partitionKey = projectId, deterministic MessageId carried in the
/// headers so the transport's idempotency store dedupes double-publication.
/// Publication is a hint: failures are logged and swallowed — a bus hiccup
/// must never break the DB mutation that triggered the event (the 15m
/// backstop ticks re-derive anything a lost hint would have triggered).
/// </summary>
public sealed class TalariaEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ILogger<TalariaEventPublisher> _logger;
    private readonly ConcurrentDictionary<Type, object> _producers = new();

    public TalariaEventPublisher(ITransport transport, ILogger<TalariaEventPublisher> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
    {
        try
        {
            var producer = await GetProducerAsync<T>(ct);
            var headers = new MessageHeaders { MessageId = evt.MessageId };
            await producer.ProduceAsync(evt, headers, partitionKey: evt.ProjectId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — not a hint-loss warning; let cancellation propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event publish failed (hint lost; backstop tick will re-derive): {EventType} {MessageId}",
                typeof(T).Name, evt.MessageId);
        }
    }

    private async Task<IProducer<T>> GetProducerAsync<T>(CancellationToken ct) where T : IForgeEvent
    {
        if (_producers.TryGetValue(typeof(T), out var cached))
            return (IProducer<T>)cached;

        var created = await _transport.CreateProducerAsync<T>(Topics.For<T>(), new ProducerOptions(), ct);
        return (IProducer<T>)_producers.GetOrAdd(typeof(T), created);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var producer in _producers.Values)
        {
            if (producer is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
        _producers.Clear();
    }
}
