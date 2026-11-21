namespace Forge.Core.Messaging;

/// <summary>External or operator hint. Never contains a verdict: the watcher re-reads GitHub.</summary>
public sealed record PrWatchRequested : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public string? TaskId { get; init; }
    public int? PrNumber { get; init; }
    public string? HeadSha { get; init; }
    public string? Branch { get; init; }
    public required string Reason { get; init; }
}

public static class PrWatchWakeup
{
    /// <summary>Call after a saved task hold/release; the owning watch re-evaluates live policy.</summary>
    public static Task RequestAsync(IEventPublisher publisher, string projectId, string taskId,
        string reason, CancellationToken ct = default)
        => publisher.PublishAsync(new PrWatchRequested
        {
            MessageId = $"pr-watch:{projectId}:{taskId}:{Guid.NewGuid():N}",
            ProjectId = projectId, TaskId = taskId, Reason = reason,
        }, ct);
}
