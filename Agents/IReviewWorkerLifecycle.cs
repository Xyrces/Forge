namespace Forge.Agents;

/// <summary>Optional durable worker lifecycle for callers that retry a review of the same head.</summary>
public interface IReviewWorkerLifecycle
{
    Task<ReviewWorkerState?> InspectReviewWorkerAsync(Core.AgentType role, string projectId, string taskId,
        string dispatchId, bool stop, CancellationToken ct);
    Task<bool> HasUntrackedReviewWorkerAsync(Core.AgentType role, string projectId, string taskId, CancellationToken ct);
}

public sealed record ReviewWorkerState(bool Terminal, string WorktreePath, IReadOnlyDictionary<string, string> Context);
