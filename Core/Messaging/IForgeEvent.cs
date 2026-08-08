namespace Forge.Core.Messaging;

/// <summary>
/// Marker contract for every internal coordination event flowing through
/// the messaging seam. Pure data — no behavior, no dependencies.
/// <see cref="MessageId"/> must be deterministic (derived from the
/// event's natural key) so the transport's idempotency store dedupes
/// double-publication.
/// </summary>
public interface IForgeEvent
{
    string MessageId { get; }

    string ProjectId { get; }
}
