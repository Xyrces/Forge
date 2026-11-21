namespace Forge.Core.Completion;

public enum EngineeringOutcome { PendingEvidence, Satisfied, Incomplete, LegacyTerminalOnly }

public sealed record SprintCompletionRecord(
    string Id, string ProjectId, string SprintId, long? PlanAuditId, string? PlanHash,
    EngineeringOutcome EngineeringOutcome, string MembersJson, string EvidenceJson,
    string BookkeepingState, string? SourceSha, string? DeploymentCandidateId,
    DateTime CreatedAt, DateTime UpdatedAt, bool ReadyForDeployment, string? DeliveryNote,
    string? SourceEvidenceJson = null, string? AbandonmentJson = null, bool DeliveryRequired = false, string? CompletionPolicyJson = null, string? ReconciliationJson = null)
{
    public CompletionPolicySnapshot? CompletionPolicy => CompletionPolicyJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<CompletionPolicySnapshot>(CompletionPolicyJson);
}

public sealed record CompletionTriageBatch(
    string Id, string CompletionId, string State, string SourcesJson,
    string? DecisionJson, string? Error, DateTime UpdatedAt);

public sealed record CompletionEvidence(
    long? PlanAuditId, string? PlanHash, EngineeringOutcome Outcome, string EvidenceJson,
    bool ReadyForDeployment = false, bool DeliveryRequired = false, CompletionPolicySnapshot? CompletionPolicy = null);

public sealed record CompletionMaterializationResult(string? TargetId, bool Created);

public sealed record CompletionAbandonment(string PlanHash, string Reason, string OperatorIdentity, DateTimeOffset AbandonedAt);

public sealed record CompletionPolicySnapshot(string? EnvironmentName, bool RequireApproval, string? TargetJson);
public sealed record CompletionException(string PlanHash, string SourceSha, string DeploymentId, IReadOnlyList<string> MissedCriterionIds, string Reason, string OperatorIdentity, DateTimeOffset RecordedAt);
public sealed record ReconcileDeliveredCompletionRequest(string PlanHash, string SourceSha, string DeploymentId, IReadOnlyList<string> MissedCriterionIds, string Reason);
