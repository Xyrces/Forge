using System.Data.Common;
using System.Text.RegularExpressions;

namespace Forge.Core.Completion;

public sealed record OperatorCriterionEvidence(string PlanHash, string CriterionId, string SourceSha,
    string DeploymentId, string ProofReference, string ProofSha256);
public sealed record CompletionCriterionStatus(string Id, string Text, string EvidenceKind,
    string? ProofReference, string? OperatorIdentity, bool Blocking = true, bool MissedWithException = false);

public sealed partial class SprintCompletionStore
{
    public async Task<IReadOnlyList<CompletionCriterionStatus>> ReadCriteriaAsync(string completionId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var receipt = await GetAsync(connection, tx, completionId, ct);
        if (receipt?.PlanAuditId is null) return [];
        var plan = await ReadBoundPlanAsync(connection, tx, receipt, ct);
        var proofs = new Dictionary<string, (string Reference, string Operator)>(StringComparer.Ordinal);
        using var command = Command(connection, tx,
            $"SELECT criterion_id,proof_ref,operator_identity FROM {T("completion_criterion_evidence")} WHERE completion_id=@id AND plan_hash=@plan AND source_sha=@source AND deployment_id=@deployment",
            ("@id", receipt.Id), ("@plan", receipt.PlanHash), ("@source", receipt.SourceSha), ("@deployment", receipt.DeploymentCandidateId));
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) proofs.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
        return plan.Criteria.Select(c => new CompletionCriterionStatus(c.Id, c.Text, c.EvidenceKind.ToString(),
            proofs.TryGetValue(c.Id, out var proof) ? proof.Reference : null,
            proofs.TryGetValue(c.Id, out proof) ? proof.Operator : null,
            c.EvidenceKind != SprintEvidenceKind.OperatorObserved || receipt.CompletionPolicy is null && receipt.ReconciliationJson is null,
            receipt.ReconciliationJson is { } reconciliation && System.Text.Json.JsonSerializer.Deserialize<CompletionException>(reconciliation)!.MissedCriterionIds.Contains(c.Id))).ToArray();
    }

    private async Task<SprintPlan> ReadBoundPlanAsync(DbConnection connection, DbTransaction tx,
        SprintCompletionRecord receipt, CancellationToken ct)
    {
        using var command = Command(connection, tx,
            $"SELECT weights_json FROM {T("sprint_proposal_audit")} WHERE id=@audit AND committed_sprint_id=@sprint",
            ("@audit", receipt.PlanAuditId), ("@sprint", receipt.SprintId));
        var json = await command.ExecuteScalarAsync(ct) as string;
        var plan = json is null ? null : SprintPlanJson.ReadAuditPlan(json);
        if (plan is null || plan.ProjectId != issues.ProjectId || plan.Hash != receipt.PlanHash)
            throw new InvalidOperationException("The exact completion plan is unavailable or changed.");
        return plan;
    }

    private async Task<bool> IsQualifiedDeploymentAsync(DbConnection connection, DbTransaction tx,
        SprintCompletionRecord receipt, CancellationToken ct)
    {
        using var command = Command(connection, tx,
            $"SELECT environment_name,target_json,source_candidate_id FROM {T("deployment")} WHERE id=@deployment AND completion_id=@completion AND commit_sha=@source AND status='Deployed' AND receipt_json IS NOT NULL",
            ("@deployment", receipt.DeploymentCandidateId), ("@completion", receipt.Id), ("@source", receipt.SourceSha));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        var policy = receipt.CompletionPolicy;
        return policy is null || !reader.IsDBNull(0) && !reader.IsDBNull(1) && reader.IsDBNull(2)
            && string.Equals(reader.GetString(0), policy.EnvironmentName, StringComparison.Ordinal)
            && string.Equals(reader.GetString(1), policy.TargetJson, StringComparison.Ordinal);
    }

    /// <summary>Explicit external verification, never an automatic visual check or generic success stamp.</summary>
    public async Task RecordOperatorEvidenceAsync(string completionId, OperatorCriterionEvidence evidence,
        string authenticatedOperator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedOperator) || authenticatedOperator.Length > 128
            || string.IsNullOrWhiteSpace(evidence.ProofReference) || evidence.ProofReference.Length > 4096
            || !Regex.IsMatch(evidence.ProofSha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new ArgumentException("Authenticated operator provenance and a bounded, hashed proof reference are required.");
        var proofHash = evidence.ProofSha256!.ToLowerInvariant();
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        var receipt = await GetAsync(connection, tx, completionId, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (!receipt.ReadyForDeployment || receipt.PlanHash != evidence.PlanHash || receipt.SourceSha != evidence.SourceSha
            || receipt.DeploymentCandidateId != evidence.DeploymentId || !await IsQualifiedDeploymentAsync(connection, tx, receipt, ct))
            throw new InvalidOperationException("Operator evidence must identify the exact qualified deployed completion source.");
        var plan = await ReadBoundPlanAsync(connection, tx, receipt, ct);
        if (!plan.Criteria.Any(c => c.Id == evidence.CriterionId && c.EvidenceKind == SprintEvidenceKind.OperatorObserved))
            throw new InvalidOperationException("Only an explicit operator-observed criterion may receive this evidence.");
        using (var prior = Command(connection, tx,
            $"SELECT plan_hash,source_sha,deployment_id,proof_ref,proof_sha256,operator_identity FROM {T("completion_criterion_evidence")} WHERE completion_id=@id AND criterion_id=@criterion",
            ("@id", completionId), ("@criterion", evidence.CriterionId)))
        {
            await using var reader = await prior.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != evidence.PlanHash || reader.GetString(1) != evidence.SourceSha
                    || reader.GetString(2) != evidence.DeploymentId || reader.GetString(3) != evidence.ProofReference
                    || reader.GetString(4) != proofHash || reader.GetString(5) != authenticatedOperator)
                    throw new InvalidOperationException("Recorded criterion evidence is immutable.");
                return;
            }
        }
        using var insert = Command(connection, tx,
            $"INSERT INTO {T("completion_criterion_evidence")}(completion_id,criterion_id,plan_hash,source_sha,deployment_id,proof_ref,proof_sha256,operator_identity,observed_at) VALUES(@id,@criterion,@plan,@source,@deployment,@proof,@hash,@operator,@now)",
            ("@id", completionId), ("@criterion", evidence.CriterionId), ("@plan", evidence.PlanHash),
            ("@source", evidence.SourceSha), ("@deployment", evidence.DeploymentId), ("@proof", evidence.ProofReference),
            ("@hash", proofHash), ("@operator", authenticatedOperator), ("@now", Now()));
        await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> RefreshDeploymentOutcomeAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        return await FinishDeploymentTransactionAsync(connection, tx, id, ct);
    }

    internal async Task CommitVerifiedTargetAsync(string id, string deploymentId, string log, CancellationToken ct)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        using var status = Command(connection, tx,
            $"UPDATE {T("deployment")} SET status='Deployed',deployed_at=@now,deploy_log=@log WHERE id=@deploy AND project_id=@project AND completion_id=@id AND environment_name IS NOT NULL AND status IN ('Approved','Deploying')",
            ("@now", Now()), ("@log", log), ("@deploy", deploymentId), ("@project", issues.ProjectId), ("@id", id));
        if (await status.ExecuteNonQueryAsync(ct) == 0) return; // Concurrent reconciliation already won.
        if (!await FinishDeploymentTransactionAsync(connection, tx, id, ct))
            throw new InvalidOperationException("Verified target does not match the authorized sprint completion. The deployment transaction was rolled back.");
    }

    private async Task<bool> FinishDeploymentTransactionAsync(DbConnection connection, DbTransaction tx, string id, CancellationToken ct)
    {
        var receipt = await GetAsync(connection, tx, id, ct);
        if (receipt is null || receipt.EngineeringOutcome != EngineeringOutcome.PendingEvidence
            || !receipt.ReadyForDeployment || !await IsQualifiedDeploymentAsync(connection, tx, receipt, ct)) return false;
        var plan = await ReadBoundPlanAsync(connection, tx, receipt, ct);
        foreach (var criterion in plan.Criteria.Where(c => receipt.CompletionPolicy is null && c.EvidenceKind == SprintEvidenceKind.OperatorObserved))
        {
            using var evidence = Command(connection, tx,
                $"SELECT COUNT(*) FROM {T("completion_criterion_evidence")} WHERE completion_id=@id AND criterion_id=@criterion AND plan_hash=@plan AND source_sha=@source AND deployment_id=@deployment",
                ("@id", id), ("@criterion", criterion.Id), ("@plan", receipt.PlanHash),
                ("@source", receipt.SourceSha), ("@deployment", receipt.DeploymentCandidateId));
            if (Convert.ToInt32(await evidence.ExecuteScalarAsync(ct)) != 1) return false;
        }
        using var update = Command(connection, tx,
            $"UPDATE {T("sprint_completion")} SET outcome='Satisfied',delivery_note='Required deployment and acceptance checks passed.',updated_at=@now WHERE id=@id",
            ("@now", Now()), ("@id", id));
        await update.ExecuteNonQueryAsync(ct);
        if (receipt.CompletionPolicy is not null)
        {
            using var finish = Command(connection, tx, $"UPDATE {T("sprint")} SET status='Completed',updated_at=@now WHERE id=@sprint AND status='Active'", ("@now", Now()), ("@sprint", receipt.SprintId));
            await finish.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        await PublishOutcomeChangedAsync(receipt, "Satisfied", ct);
        return true;
    }

    private Task PublishOutcomeChangedAsync(SprintCompletionRecord receipt, string outcome, CancellationToken ct)
        => issues.Events.PublishAsync(new Messaging.SprintOutcomeChanged
        {
            MessageId = $"sprint-outcome:{issues.ProjectId}:{receipt.Id}:{outcome}",
            ProjectId = issues.ProjectId, SprintId = receipt.SprintId,
            CompletionId = receipt.Id, Outcome = outcome,
        }, ct);
}
