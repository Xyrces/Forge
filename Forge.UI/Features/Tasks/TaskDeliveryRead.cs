namespace Forge.Dashboard.Features.Tasks;

public sealed record TaskScopeCriterion(string Text, string EvidenceKind);
public sealed record TaskDeliveryRead(IReadOnlyList<TaskScopeCriterion> Criteria,
    string? VerifiedHead, DateTimeOffset? VerifiedAt, string? MergeCommit, DateTimeOffset? MergeObservedAt, string? ExistingSource = null, DateTimeOffset? ExistingVerifiedAt = null, DateTimeOffset? ExistingReviewedAt = null);
