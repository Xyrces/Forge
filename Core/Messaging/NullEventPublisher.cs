namespace Forge.Core.Messaging;

/// <summary>No-op publisher — the Core default so stores and Core tests stay dependency-free.</summary>
public sealed class NullEventPublisher : IEventPublisher
{
    public static readonly NullEventPublisher Instance = new();

    private NullEventPublisher() { }

    public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        => Task.CompletedTask;
}
