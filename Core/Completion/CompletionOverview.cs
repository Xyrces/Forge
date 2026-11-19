using System.Text.Json;

namespace Forge.Core.Completion;

public sealed record CompletionOverview(string Id, string ProjectId, string SprintId, string Outcome,
    string BookkeepingState, bool ReadyForDeployment, string? SourceSha, string? DeploymentCandidateId,
    string? DeploymentStatus, string? DeliveryNote, string? PlanHash, string SprintName,
    string? AbandonmentReason, DateTimeOffset? AbandonedAt, bool DeliveryRequired, CompletionPolicySnapshot? CompletionPolicy = null, bool DeploymentApprovalRequired = false, CompletionException? Reconciliation = null);

public sealed partial class SprintCompletionStore
{
    /// <summary>One bounded joined query; no member/evidence bodies or per-row deployment lookups.</summary>
    public async Task<IReadOnlyList<CompletionOverview>> ListOverviewAsync(int limit = 20, string? afterId = null,
        bool waitingOnly = true, string? completionId = null, CancellationToken ct = default)
    {
        var d = issues.Db.Dialect;
        var take = Math.Clamp(limit, 1, 100);
        var c = T("sprint_completion");
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null,
            $"SELECT {d.Top(take)}{c}.id,{c}.project_id,{c}.sprint_id,{c}.outcome,CASE WHEN {c}.abandonment_json IS NULL AND ({c}.completion_policy_json IS NULL OR ({c}.deployment_candidate_id IS NULL AND {c}.outcome <> 'Satisfied')) AND EXISTS(SELECT 1 FROM {T("completion_triage")} review WHERE review.completion_id={c}.id AND review.state='NeedsReview') THEN 'NeedsReview' ELSE {c}.bookkeeping_state END,{c}.ready_for_deployment,{c}.source_sha,{c}.deployment_candidate_id,dep.status,{c}.delivery_note,{c}.plan_hash,s.name,{c}.abandonment_json,{c}.delivery_required,{c}.completion_policy_json,{c}.completion_gate_approval_json,{c}.reconciliation_json FROM {c} LEFT JOIN {T("sprint")} s ON s.id={c}.sprint_id LEFT JOIN {T("deployment")} dep ON dep.id={c}.deployment_candidate_id AND dep.completion_id={c}.id WHERE "
            + (waitingOnly ? CompletionPlanningBarrier.Predicate(d, includeLegacyIncomplete: true) : "1=1")
            + (afterId is null ? "" : $" AND {c}.id>@after")
            + (completionId is null ? "" : $" AND {c}.id=@id")
            + $" ORDER BY {c}.id{d.Limit(take)}", ("@after", afterId), ("@id", completionId));
        var result = new List<CompletionOverview>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? Optional(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            var policy = Optional(14) is { } policyJson ? JsonSerializer.Deserialize<CompletionPolicySnapshot>(policyJson) : null;
            var reconciliation = Optional(16) is { } reconciliationJson ? JsonSerializer.Deserialize<CompletionException>(reconciliationJson) : null;
            var abandonment = Optional(12) is { } json ? JsonSerializer.Deserialize<CompletionAbandonment>(json) : null;
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Convert.ToInt32(reader.GetValue(5)) != 0, Optional(6), Optional(7), Optional(8),
                Optional(9) ?? (policy is not null && Optional(7) is null && reader.GetString(4) != "Completed" ? reader.GetString(4) == "NeedsReview" ? "Follow-up triage needs review before deployment can begin." : "Preparing follow-ups before deployment; UAT is the final sprint step." : null), Optional(10), Optional(11) ?? "Missing sprint", abandonment?.Reason, abandonment?.AbandonedAt, Convert.ToInt32(reader.GetValue(13)) != 0, policy, policy?.RequireApproval == true && Optional(15) is null && abandonment is null && Optional(7) is null && reader.GetString(3) == "PendingEvidence", reconciliation));
        }
        return result;
    }
}
