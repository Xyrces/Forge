using System.Text.Json;
using Forge.Core.Completion;

namespace Forge.Core;

public sealed partial class IssueStore
{
    /// <summary>Controller-only admission of a completed isolated verifier receipt; retains original publication.</summary>
    public async Task AdmitIntegrationVerificationAsync(string taskId, IntegratedHeadVerificationEvidence proof, CancellationToken ct)
    {
        var task = await GetAsync(taskId, ct) ?? throw new InvalidOperationException("Task disappeared.");
        var original = JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication") ?? "null");
        if (proof.ProjectId != _projectId || proof.TaskId != taskId || original?.TaskId != taskId || proof.OriginalHeadSha != original.PublishedHeadSha
            || proof.OriginalAttemptId != original.Verification.AttemptId || proof.Verification.CommandsSha != original.Verification.CommandsSha
            || !int.TryParse(task.GetMetadata("prNumber"), out var number) || number != proof.PullRequestNumber)
            throw new InvalidOperationException("Integration receipt does not bind to the original task publication.");
        await new MemoryStore(_db).RecordIntegrationVerificationAsync(proof, ct);
        await PatchMetadataOnlyAsync(taskId, new Dictionary<string, object>
        { ["trustedIntegrationVerification"] = JsonSerializer.Serialize(proof), ["reviewIntegrationVerificationError"] = null! },
            "Trusted isolated verification recorded for integrated head " + proof.VerifiedHeadSha, ct);
    }
}
