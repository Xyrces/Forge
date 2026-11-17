using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Core.Completion;

public sealed record TrustedPublicationEvidence(string TaskId, string PublishedHeadSha, VerificationReceipt Verification);
public sealed record ObservedMergeEvidence(string TaskId, int PullRequestNumber, string HeadSha, string MergeSha, DateTimeOffset ObservedAt);

public static class TaskDeliveryEvidence
{
    public static bool IsImplementationVerified(IssueRecord task, SprintPlan? plan) =>
        IsMergedAndVerified(task) || (plan is not null && ExistingImplementationEvidence.TryGet(task, plan, out _));

    public static bool IsMergedAndVerified(IssueRecord task)
    {
        try
        {
            var publication = JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication") ?? "null");
            var merge = JsonSerializer.Deserialize<ObservedMergeEvidence>(task.GetMetadata("observedMerge") ?? "null");
            return task.Status == IssueStatus.Completed && publication?.Verification is not null && merge is not null
                && publication.TaskId == task.Id && merge.TaskId == task.Id
                && publication.PublishedHeadSha == merge.HeadSha
                && int.TryParse(task.GetMetadata("prNumber"), out var number) && number == merge.PullRequestNumber
                && task.GetMetadata("branchSha") == merge.HeadSha
                && Regex.IsMatch(merge.MergeSha ?? "", "^[a-fA-F0-9]{40}$")
                && Regex.IsMatch(merge.HeadSha ?? "", "^[a-fA-F0-9]{40}$")
                && !string.IsNullOrWhiteSpace(publication.Verification.AttemptId)
                && Regex.IsMatch(publication.Verification.TreeSha ?? "", "^[a-fA-F0-9]{40}$")
                && Regex.IsMatch(publication.Verification.CommandsSha ?? "", "^[a-fA-F0-9]{64}$");
        }
        catch (JsonException) { return false; }
    }
}
