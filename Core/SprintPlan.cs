using System.Security.Cryptography;
using System.Text.Json;

namespace Forge.Core;

public enum SprintEvidenceKind { MergedAndVerified, DeploymentHealthy }
public enum SprintMemberReason { Delivers, Prerequisite, Remediates }
public sealed record SprintScopeReference(string SpecId, string ProposalId, int Revision, string Hash, string ApprovedExcerpt);
public sealed record SprintCriterion(string Id, string Text, SprintEvidenceKind EvidenceKind, SprintScopeReference Scope);
public sealed record SprintPlanMember(string TaskId, IReadOnlyList<string> CriterionIds, SprintMemberReason Reason);
public sealed record SprintPlanDeferral(string TaskId, string Reason);
/// <summary>Grooming's explicit delivery unit. The complete required member set prevents partial criterion coverage.</summary>
public sealed record SprintOutcomeBinding(int Version, string OutcomeId, string Objective,
    IReadOnlyList<SprintCriterion> Criteria, IReadOnlyList<string> RequiredTaskIds,
    IReadOnlyList<string> CriterionIds, SprintMemberReason Reason, SprintScopeReference Authority);
public sealed record SprintPlan(int Version, string ProjectId, string Objective,
    IReadOnlyList<SprintCriterion> Criteria, IReadOnlyList<SprintPlanMember> Members,
    IReadOnlyList<SprintPlanDeferral> Deferred, string InputFingerprint, string Hash)
{
    public static string ComputeHash(SprintPlan plan) => SprintPlanJson.Hash(plan with { Hash = "" });
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidHash => Version == 1 && Hash == ComputeHash(this);
}

public static class SprintPlanJson
{
    public const string MetadataKey = "sprintOutcome";
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options)));
    public static SprintPlan? ReadAuditPlan(string weightsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(weightsJson);
            if (!document.RootElement.TryGetProperty("plan", out var value)) return null;
            var plan = value.Deserialize<SprintPlan>(Options);
            return plan is { Version: 1, Members: not null, Criteria: not null, Deferred: not null }
                && plan.HasValidHash ? plan : null;
        }
        catch (JsonException) { return null; }
    }
    public static SprintOutcomeBinding? ReadBinding(IssueRecord issue)
    {
        var json = issue.GetMetadata(MetadataKey);
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32768) return null;
        try { return JsonSerializer.Deserialize<SprintOutcomeBinding>(json, Options); }
        catch (JsonException) { return null; }
    }
}
