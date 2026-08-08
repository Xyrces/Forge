namespace Forge.Core.Messaging;

/// <summary>
/// Publication seam for internal coordination events. Core stores
/// publish AFTER a mutation commits — publication is a hint, never the
/// source of truth, so implementations must never let a publish failure
/// break the mutation that triggered it. Default in Core is
/// <see cref="NullEventPublisher"/>; the Messaging/ module provides the
/// Talaria-backed implementation. No Talaria reference in Core.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent;
}
