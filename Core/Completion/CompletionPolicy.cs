using System.Text.Json;

namespace Forge.Core.Completion;

public sealed partial class SprintCompletionStore
{
    public async Task<CompletionPolicySnapshot?> ReadSprintPolicyAsync(string sprintId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"SELECT completion_policy_json FROM {T("sprint")} WHERE id=@id", ("@id", sprintId));
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<CompletionPolicySnapshot>(json) : null;
    }

    private string DeploymentApprovalPredicate => issues.Db.Dialect.Provider == Forge.Core.Db.ForgeDbProvider.SqlServer
        ? "(completion_policy_json IS NULL OR JSON_VALUE(completion_policy_json, '$.RequireApproval')='false' OR completion_gate_approval_json IS NOT NULL)"
        : "(completion_policy_json IS NULL OR json_extract(completion_policy_json, '$.RequireApproval')=0 OR completion_gate_approval_json IS NOT NULL)";

    public async Task<bool> CanAdmitDeploymentAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null,
            $"SELECT completion_policy_json,completion_gate_approval_json FROM {T("sprint_completion")} WHERE id=@id AND ready_for_deployment=1 AND delivery_required=1 AND abandonment_json IS NULL AND (completion_policy_json IS NULL OR deployment_candidate_id IS NOT NULL OR {CompletionPlanningBarrier.BookkeepingReadyPredicate(issues.Db.Dialect)})", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        var policy = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<CompletionPolicySnapshot>(reader.GetString(0));
        return policy?.RequireApproval != true || !reader.IsDBNull(1);
    }

    public async Task ApproveDeploymentAsync(string id, string planHash, string authenticatedOperator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedOperator) || authenticatedOperator.Length > 128)
            throw new ArgumentException("Authenticated operator provenance is required.");
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        var receipt = await GetAsync(connection, tx, id, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (receipt.CompletionPolicy?.RequireApproval != true || receipt.PlanHash != planHash || !receipt.ReadyForDeployment
            || receipt.AbandonmentJson is not null || receipt.DeploymentCandidateId is not null)
            throw new InvalidOperationException("Only the exact verified completion awaiting its configured deployment approval can be approved.");
        using var update = Command(connection, tx,
            $"UPDATE {T("sprint_completion")} SET completion_gate_approval_json=@approval,updated_at=@now WHERE id=@id AND completion_gate_approval_json IS NULL",
            ("@id", id), ("@now", Now()), ("@approval", JsonSerializer.Serialize(new { PlanHash = planHash, receipt.CompletionPolicy, OperatorIdentity = authenticatedOperator, ApprovedAt = DateTimeOffset.UtcNow })));
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        await PublishOutcomeChangedAsync(receipt, "DeploymentApproved", ct);
    }

    /// <summary>Explicit legacy exception: preserve the missed observation and original deployment facts.</summary>
    public async Task ReconcileDeliveredAsync(string id, ReconcileDeliveredCompletionRequest request, string authenticatedOperator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedOperator) || authenticatedOperator.Length > 128
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000
            || request.MissedCriterionIds is not { Count: > 0 } || request.MissedCriterionIds.Distinct(StringComparer.Ordinal).Count() != request.MissedCriterionIds.Count)
            throw new ArgumentException("Explicit missed criteria, bounded reason and authenticated operator provenance are required.");
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        var receipt = await GetAsync(connection, tx, id, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (receipt.PlanHash != request.PlanHash || receipt.SourceSha != request.SourceSha || receipt.DeploymentCandidateId != request.DeploymentId
            || receipt.CompletionPolicy is not null || receipt.AbandonmentJson is not null || !await IsQualifiedDeploymentAsync(connection, tx, receipt, ct))
            throw new InvalidOperationException("Reconciliation requires the exact qualified legacy deployment; original facts cannot change.");
        if (receipt.ReconciliationJson is { } recorded)
        {
            var prior = JsonSerializer.Deserialize<CompletionException>(recorded)!;
            if (prior.Reason != request.Reason || prior.OperatorIdentity != authenticatedOperator || !prior.MissedCriterionIds.SequenceEqual(request.MissedCriterionIds))
                throw new InvalidOperationException("The reconciliation audit is immutable.");
            return;
        }
        if (receipt.EngineeringOutcome != EngineeringOutcome.PendingEvidence)
            throw new InvalidOperationException("Only an unresolved delivered legacy completion may be reconciled.");
        var plan = await ReadBoundPlanAsync(connection, tx, receipt, ct);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var criterion in plan.Criteria.Where(c => c.EvidenceKind == SprintEvidenceKind.OperatorObserved))
        {
            using var proof = Command(connection, tx,
                $"SELECT COUNT(*) FROM {T("completion_criterion_evidence")} WHERE completion_id=@id AND criterion_id=@criterion AND plan_hash=@plan AND source_sha=@source AND deployment_id=@deployment",
                ("@id", id), ("@criterion", criterion.Id), ("@plan", receipt.PlanHash), ("@source", receipt.SourceSha), ("@deployment", receipt.DeploymentCandidateId));
            if (Convert.ToInt32(await proof.ExecuteScalarAsync(ct)) != 1) missing.Add(criterion.Id);
        }
        if (!missing.SetEquals(request.MissedCriterionIds))
            throw new InvalidOperationException("The exception must name every missing operator criterion exactly; observed criteria cannot be rewritten.");
        var audit = new CompletionException(request.PlanHash, request.SourceSha, request.DeploymentId, request.MissedCriterionIds, request.Reason, authenticatedOperator, DateTimeOffset.UtcNow);
        using var update = Command(connection, tx,
            $"UPDATE {T("sprint_completion")} SET outcome='Satisfied',reconciliation_json=@audit,delivery_note=@note,updated_at=@now WHERE id=@id",
            ("@id", id), ("@now", Now()), ("@audit", JsonSerializer.Serialize(audit)),
            ("@note", "Delivered with explicit operator exception: " + request.Reason));
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        await PublishOutcomeChangedAsync(receipt, "Satisfied", ct);
    }
}
