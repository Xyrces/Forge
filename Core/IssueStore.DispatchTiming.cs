namespace Forge.Core;

public sealed partial class IssueStore
{
    /// <summary>Called only after sprint/dependency/admission checks, before competing for a worker slot.</summary>
    public async Task ObserveDispatchEligibleAsync(IssueRecord task, string activeSprintId, CancellationToken ct)
    {
        var key = string.Join("|", activeSprintId, task.GetMetadata("reworkForSha"),
            task.GetMetadata("reworkAttempts"), task.GetMetadata("requeuedFromFailedAt"));
        if (task.GetMetadata("dispatchEligibilityKey") == key) return;
        var current = await GetAsync(task.Id, ct);
        if (current is null || current.Status != IssueStatus.Pending || current.GetMetadata("dispatchEligibilityKey") == key) return;
        await PatchMetadataOnlyAsync(task.Id, new Dictionary<string, object>
        {
            ["dispatchEligibilityKey"] = key,
            ["dispatchEligibleAt"] = DateTimeOffset.UtcNow.ToString("O")
        }, null, ct);
    }
}
