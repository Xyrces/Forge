using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Core.Completion;

public sealed record ExistingImplementationReview(string TaskId, string ProjectId, string PlanHash,
    string SourceSha, string TreeSha, string CommandsSha, IReadOnlyList<string> CriterionIds,
    string Verdict, string ReviewerAttemptId, DateTimeOffset ReviewedAt);

public sealed record VerifiedExistingImplementationEvidence(string TaskId, string ProjectId, string PlanHash,
    string SourceSha, IReadOnlyList<SprintCriterion> Criteria, VerificationReceipt Verification,
    ExistingImplementationReview Review);

public static class ExistingImplementationEvidence
{
    public const string MetadataKey = "trustedExistingImplementation";

    public static bool TryGet(IssueRecord task, SprintPlan plan, out VerifiedExistingImplementationEvidence? evidence)
    {
        evidence = null;
        try
        {
            var value = JsonSerializer.Deserialize<VerifiedExistingImplementationEvidence>(task.GetMetadata(MetadataKey) ?? "null");
            if (task.Status != IssueStatus.Completed || !plan.HasValidHash || value?.Verification is not { } verification
                || value.Review is not { } review || value.Criteria is null || review.CriterionIds is null
                || value.TaskId != task.Id || value.ProjectId != plan.ProjectId || value.PlanHash != plan.Hash
                || !Hex(value.SourceSha, 40) || !Hex(verification.TreeSha, 40) || !Hex(verification.CommandsSha, 64)
                || string.IsNullOrWhiteSpace(verification.AttemptId) || verification.CompletedAt == default
                || review.TaskId != task.Id || review.ProjectId != plan.ProjectId || review.PlanHash != plan.Hash
                || review.SourceSha != value.SourceSha || review.TreeSha != verification.TreeSha || review.CommandsSha != verification.CommandsSha
                || review.Verdict != "Approve" || string.IsNullOrWhiteSpace(review.ReviewerAttemptId)
                || review.ReviewerAttemptId == verification.AttemptId || review.ReviewedAt < verification.CompletedAt)
                return false;
            var member = plan.Members.SingleOrDefault(m => m.TaskId == task.Id);
            if (member is null) return false;
            var criteria = plan.Criteria.Where(c => member.CriterionIds.Contains(c.Id)
                && c.EvidenceKind is SprintEvidenceKind.MergedAndVerified or SprintEvidenceKind.ImplementationVerified).ToArray();
            if (criteria.Length == 0 || criteria.Any(c => c.EvidenceKind != SprintEvidenceKind.ImplementationVerified)
                || value.Criteria.Count != criteria.Length || value.Criteria.Distinct().Count() != criteria.Length
                || !criteria.All(value.Criteria.Contains) || review.CriterionIds.Count != criteria.Length
                || !criteria.Select(c => c.Id).ToHashSet(StringComparer.Ordinal).SetEquals(review.CriterionIds))
                return false;
            evidence = value;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool Hex(string? value, int length) => value is not null
        && Regex.IsMatch(value, "^[a-f0-9]{" + length + "}$");
}
