namespace Forge.Core;

/// <summary>Review-service faults are operator work, never engineering rework strikes.</summary>
public static class ReviewIntegrationState
{
    public const string BlockedKind = "review-integration";
    public const int MaxAttempts = 3;
    public static bool IsBlocked(IssueRecord task) => task.Status == IssueStatus.Blocked && task.GetMetadata("blockedKind") == BlockedKind;
    public static bool Exhausted(IssueRecord task, string head) =>
        (task.GetMetadata("reviewSha") == head && task.GetMetadata("reviewVerdict") == "Error"
            && int.TryParse(task.GetMetadata("reviewErrorAttempts"), out var model) && model >= MaxAttempts)
        || (task.GetMetadata("reviewIntegrationHead") == head
            && int.TryParse(task.GetMetadata("reviewIntegrationAttempts"), out var attempts) && attempts >= MaxAttempts);

    public static Dictionary<string, object> Failure(IssueRecord task, string head, string error, bool uncertain = false) => new()
    {
        ["reviewIntegrationHead"] = head,
        ["reviewIntegrationAttempts"] = uncertain ? MaxAttempts :
            task.GetMetadata("reviewIntegrationHead") == head && int.TryParse(task.GetMetadata("reviewIntegrationAttempts"), out var count) ? count + 1 : 1,
        ["reviewIntegrationError"] = error,
        ["reviewStartedAt"] = null!
    };

    // Never clear a saved draft, publication intent, human hold or engineering strike.
    public static Dictionary<string, object> RetryMetadata() => new()
    {
        ["blockedKind"] = null!, ["reviewIntegrationAttempts"] = 0, ["reviewErrorAttempts"] = 0,
        ["reviewIntegrationError"] = null!, ["reviewStartedAt"] = null!,
        ["clearanceAction"] = "operator-retry-review", ["lastError"] = null!
    };
}
