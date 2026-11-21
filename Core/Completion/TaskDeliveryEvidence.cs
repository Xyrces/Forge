using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Core.Completion;

public sealed record TrustedPublicationEvidence(string TaskId, string PublishedHeadSha, VerificationReceipt Verification);
public sealed record ObservedMergeEvidence(string TaskId, int PullRequestNumber, string HeadSha, string MergeSha, DateTimeOffset ObservedAt);

public sealed record IntegratedHeadVerificationEvidence(string TaskId, string ProjectId, string Repository,
    int PullRequestNumber, string OriginalHeadSha, string VerifiedHeadSha, string OriginalAttemptId,
    string BaseParentSha, VerificationReceipt Verification);

public static class TaskDeliveryEvidence
{
    public static string IntegrationAuthorityKey(string projectId, string taskId, string head) =>
        "review/origins/integrated-head/v1/" + Uri.EscapeDataString(projectId) + "/" + Uri.EscapeDataString(taskId) + "/" + head;

    public static IntegratedHeadVerificationEvidence? Integration(IssueRecord task, string head)
    {
        try
        {
            var proof = JsonSerializer.Deserialize<IntegratedHeadVerificationEvidence>(task.GetMetadata("trustedIntegrationVerification") ?? "null");
            var original = JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication") ?? "null");
            return proof is not null && original is not null && proof.Verification is not null
                && proof.TaskId == task.Id && original.TaskId == task.Id
                && proof.OriginalHeadSha == original.PublishedHeadSha && proof.VerifiedHeadSha == head
                && proof.OriginalAttemptId == original.Verification?.AttemptId
                && proof.Verification.CommandsSha == original.Verification?.CommandsSha
                && !string.IsNullOrWhiteSpace(proof.ProjectId) && !string.IsNullOrWhiteSpace(proof.Repository)
                && int.TryParse(task.GetMetadata("prNumber"), out var number) && number == proof.PullRequestNumber
                && task.GetMetadata("branchSha") == original.PublishedHeadSha
                && Regex.IsMatch(proof.BaseParentSha ?? "", "^[a-f0-9]{40}$")
                && Regex.IsMatch(proof.Verification.TreeSha ?? "", "^[a-f0-9]{40}$")
                && Regex.IsMatch(proof.Verification.CommandsSha ?? "", "^[a-f0-9]{64}$")
                && !string.IsNullOrWhiteSpace(proof.Verification.AttemptId) ? proof : null;
        }
        catch (JsonException) { return null; }
    }

    public static bool IsImplementationVerified(IssueRecord task, SprintPlan? plan) =>
        (IsMergedAndVerified(task) && (plan is null || Integration(task, JsonSerializer.Deserialize<ObservedMergeEvidence>(task.GetMetadata("observedMerge") ?? "null")?.HeadSha ?? "") is not { } integrated || integrated.ProjectId == plan.ProjectId)) || (plan is not null && ExistingImplementationEvidence.TryGet(task, plan, out _));

    public static bool IsMergedAndVerified(IssueRecord task)
    {
        try
        {
            var publication = JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication") ?? "null");
            var merge = JsonSerializer.Deserialize<ObservedMergeEvidence>(task.GetMetadata("observedMerge") ?? "null");
            return task.Status == IssueStatus.Completed && publication?.Verification is not null && merge is not null
                && publication.TaskId == task.Id && merge.TaskId == task.Id
                && (publication.PublishedHeadSha == merge.HeadSha || Integration(task, merge.HeadSha) is not null)
                && int.TryParse(task.GetMetadata("prNumber"), out var number) && number == merge.PullRequestNumber
                && (task.GetMetadata("branchSha") == merge.HeadSha || Integration(task, merge.HeadSha) is not null)
                && Regex.IsMatch(merge.MergeSha ?? "", "^[a-fA-F0-9]{40}$")
                && Regex.IsMatch(merge.HeadSha ?? "", "^[a-fA-F0-9]{40}$")
                && !string.IsNullOrWhiteSpace(publication.Verification.AttemptId)
                && Regex.IsMatch(publication.Verification.TreeSha ?? "", "^[a-fA-F0-9]{40}$")
                && Regex.IsMatch(publication.Verification.CommandsSha ?? "", "^[a-fA-F0-9]{64}$");
        }
        catch (JsonException) { return false; }
    }
}
