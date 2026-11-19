using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Db;

namespace Forge.Core.Completion;

public sealed partial class SprintCompletionStore
{
    // Preserve the original completion snapshot; store only newly acquired authority proofs.
    private sealed record DeliveryReconciliation(string PlanHash, DateTimeOffset RecordedAt,
        string OriginalEvidenceJson, Dictionary<string, Dictionary<string, JsonElement>> MemberProofs);
    private static readonly string[] DeliveryProofKeys = ["trustedPublication", "observedMerge", "branchSha", "prNumber", "trustedIntegrationVerification"];

    public Task<IReadOnlyList<SprintCompletionRecord>> ListAwaitingImplementationEvidenceAsync(int limit = 20,
        string? afterId = null, CancellationToken ct = default) => ListAsync(
        "ready_for_deployment=0 AND outcome='PendingEvidence' AND plan_audit_id IS NOT NULL AND abandonment_json IS NULL AND source_sha IS NULL AND deployment_candidate_id IS NULL", limit, afterId, ct);

    public async Task RecordImplementationWaitAsync(string id, string note, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null,
            $"UPDATE {T("sprint_completion")} SET delivery_note=@note,updated_at=@now WHERE id=@id AND ready_for_deployment=0 AND outcome='PendingEvidence' AND abandonment_json IS NULL AND source_sha IS NULL AND deployment_candidate_id IS NULL AND (delivery_note IS NULL OR delivery_note<>@note)",
            ("@id", id), ("@note", note), ("@now", Now()));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IssueRecord>> ReadDeliveryMembersAsync(SprintCompletionRecord receipt, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var plan = await ReadBoundPlanAsync(connection, tx, receipt, ct);
        var members = JsonSerializer.Deserialize<IssueRecord[]>(receipt.MembersJson)
            ?? throw new InvalidOperationException("Frozen completion members are missing.");
        using var evidence = JsonDocument.Parse(receipt.EvidenceJson);
        if (evidence.RootElement.TryGetProperty("deliveryReconciliation", out var revised))
        {
            var proof = revised.Deserialize<DeliveryReconciliation>() ?? throw new InvalidOperationException("Delivery reconciliation is invalid.");
            if (!receipt.ReadyForDeployment || proof.PlanHash != receipt.PlanHash
                || proof.MemberProofs.Keys.Any(id => !members.Any(m => m.Id == id)))
                throw new InvalidOperationException("Delivery reconciliation does not match the frozen completion.");
            members = members.Select(m => ApplyDeliveryProofs(m, proof.MemberProofs.GetValueOrDefault(m.Id))).ToArray();
        }
        if (members.Length == 0 || members.Select(m => m.Id).Distinct().Count() != members.Length
            || !members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal).SetEquals(plan.Members.Select(m => m.TaskId))
            || members.Any(m => !TaskDeliveryEvidence.IsImplementationVerified(m, plan)))
            throw new InvalidOperationException("Completion delivery proofs do not qualify its exact members.");
        if (!await HasCanonicalIntegrationProofsAsync(connection, tx, members, plan, ct))
            throw new InvalidOperationException("Controller-owned integration verification is missing or mismatched.");
        await tx.CommitAsync(ct);
        return members;
    }

    public async Task<bool> HasRecordedIntegrationProofAsync(IssueRecord member, SprintPlan plan, CancellationToken ct = default)
    {
        if (member.GetMetadata("trustedIntegrationVerification") is null) return false;
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var present = await HasCanonicalIntegrationProofsAsync(connection, tx, [member], plan, ct);
        await tx.CommitAsync(ct);
        return present;
    }

    private async Task<bool> HasCanonicalIntegrationProofsAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction tx, IReadOnlyList<IssueRecord> members, SprintPlan plan, CancellationToken ct)
    {
        foreach (var member in members)
        {
            var projected = member.GetMetadata("trustedIntegrationVerification");
            if (projected is null) continue;
            using var proof = JsonDocument.Parse(projected);
            if (!proof.RootElement.TryGetProperty("ProjectId", out var project) || project.GetString() != issues.ProjectId
                || project.GetString() != plan.ProjectId || !proof.RootElement.TryGetProperty("VerifiedHeadSha", out var head)) return false;
            var key = TaskDeliveryEvidence.IntegrationAuthorityKey(issues.ProjectId, member.Id, head.GetString()!);
            using var read = Command(connection, tx, $"SELECT body FROM {T("memory")} WHERE [key]=@key AND ttl_days IS NULL", ("@key", key));
            if (await read.ExecuteScalarAsync(ct) as string != projected) return false;
        }
        return true;
    }

    private static IssueRecord ApplyDeliveryProofs(IssueRecord member, Dictionary<string, JsonElement>? proofs)
    {
        if (proofs is null) return member;
        var metadata = JsonNode.Parse(member.MetadataJson)!.AsObject();
        foreach (var (key, value) in proofs)
        {
            if (!DeliveryProofKeys.Contains(key) || value.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException("Only additional delivery authority can reconcile a completion.");
            if (member.GetMetadata(key) is not null && member.GetMetadata(key) != value.ToString())
                throw new InvalidOperationException("Original delivery authority cannot be replaced.");
            metadata[key] = JsonNode.Parse(value.GetRawText());
        }
        return member with { MetadataJson = metadata.ToJsonString() };
    }

    public async Task<bool> ReconcileImplementationEvidenceAsync(string id, IReadOnlyList<IssueRecord> currentMembers, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        var receipt = await GetAsync(connection, tx, id, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (receipt.ReadyForDeployment || receipt.EngineeringOutcome != EngineeringOutcome.PendingEvidence
            || receipt.PlanAuditId is null || receipt.AbandonmentJson is not null || receipt.SourceSha is not null || receipt.DeploymentCandidateId is not null) return false;
        var plan = await ReadBoundPlanAsync(connection, tx, receipt, ct);
        using (var latest = Command(connection, tx, $"SELECT MAX(id) FROM {T("sprint_proposal_audit")} WHERE committed_sprint_id=@sprint", ("@sprint", receipt.SprintId)))
            if (Convert.ToInt64(await latest.ExecuteScalarAsync(ct)) != receipt.PlanAuditId)
                throw new InvalidOperationException("Completion plan was superseded; explicit investigation required.");
        var frozen = JsonSerializer.Deserialize<IssueRecord[]>(receipt.MembersJson) ?? throw new InvalidOperationException("Frozen members unavailable.");
        var ids = frozen.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0 || ids.Count != frozen.Length || currentMembers.Count != ids.Count
            || !ids.SetEquals(currentMembers.Select(m => m.Id)) || !ids.SetEquals(plan.Members.Select(m => m.TaskId)))
            throw new InvalidOperationException("Completion membership cannot change during reconciliation.");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (var query = Command(connection, tx, $"SELECT i.id,i.type,i.status,i.updated_at,i.metadata_json FROM {T("sprint_issue")} m LEFT JOIN {T("issue")} i ON i.id=m.issue_id WHERE m.sprint_id=@sprint", ("@sprint", receipt.SprintId)))
        {
            await using var rows = await query.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                if (rows.IsDBNull(0)) throw new InvalidOperationException("Sprint member missing.");
                if (AgentTaskTypes.IsContainer(rows.GetString(1)) || rows.GetString(1) == AgentTaskTypes.PrWatch) continue;
                var current = currentMembers.SingleOrDefault(m => m.Id == rows.GetString(0));
                var original = frozen.SingleOrDefault(m => m.Id == rows.GetString(0));
                if (current is null || original is null || current.Type != original.Type || current.Status != original.Status
                    || current.Status is not (IssueStatus.Completed or IssueStatus.Closed)
                    || rows.GetString(2) != current.Status.ToString() || rows.GetString(3) != IssueStore.DateFormatTime(current.UpdatedAt)
                    || rows.GetString(4) != current.MetadataJson)
                    throw new InvalidOperationException("Completion members changed; reevaluate exact delivery evidence.");
                actual.Add(current.Id);
            }
        }
        if (!ids.SetEquals(actual)) throw new InvalidOperationException("Live membership differs from frozen completion.");
        Dictionary<string, Dictionary<string, JsonElement>> additions = [];
        foreach (var original in frozen)
        {
            var current = currentMembers.Single(m => m.Id == original.Id);
            using var metadata = JsonDocument.Parse(current.MetadataJson);
            var proofs = DeliveryProofKeys.Where(k => current.GetMetadata(k) is not null && current.GetMetadata(k) != original.GetMetadata(k))
                .ToDictionary(k => k, k => metadata.RootElement.GetProperty(k).Clone());
            var revised = ApplyDeliveryProofs(original, proofs);
            if (!TaskDeliveryEvidence.IsImplementationVerified(revised, plan)) return false;
            if (proofs.Count > 0) additions.Add(original.Id, proofs);
        }
        var derived = frozen.Select(m => ApplyDeliveryProofs(m, additions.GetValueOrDefault(m.Id))).ToArray();
        if (!await HasCanonicalIntegrationProofsAsync(connection, tx, derived, plan, ct)) return false;
        var originalEvidence = JsonNode.Parse(receipt.EvidenceJson) as JsonObject
            ?? throw new InvalidOperationException("Completion evidence must retain its original object fields.");
        if (originalEvidence.ContainsKey("deliveryReconciliation"))
            throw new InvalidOperationException("Completion delivery reconciliation is already recorded.");
        originalEvidence["deliveryReconciliation"] = JsonSerializer.SerializeToNode(
            new DeliveryReconciliation(plan.Hash, DateTimeOffset.UtcNow, receipt.EvidenceJson, additions));
        var envelope = originalEvidence.ToJsonString();
        using var update = Command(connection, tx, $"UPDATE {T("sprint_completion")} SET ready_for_deployment=1,evidence_json=@evidence,outcome=@outcome,delivery_note=NULL,updated_at=@now WHERE id=@id",
            ("@evidence", envelope), ("@now", Now()), ("@id", id), ("@outcome", receipt.CompletionPolicy is not null && !receipt.DeliveryRequired ? "Satisfied" : "PendingEvidence"));
        await update.ExecuteNonQueryAsync(ct);
        if (receipt.CompletionPolicy is not null && !receipt.DeliveryRequired)
        {
            using var finish = Command(connection, tx, $"UPDATE {T("sprint")} SET status='Completed',updated_at=@now WHERE id=@sprint AND status='Active'", ("@now", Now()), ("@sprint", receipt.SprintId));
            await finish.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (receipt.CompletionPolicy is not null && !receipt.DeliveryRequired) await PublishOutcomeChangedAsync(receipt, "Satisfied", ct);
        return true;
    }
}
