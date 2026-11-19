using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Core.Db;

namespace Forge.Core.Completion;

/// <summary>Project-local completion authority. Events are hints; receipts survive missed events.</summary>
public sealed partial class SprintCompletionStore(IssueStore issues)
{
    private string T(string table) => issues.Db.Dialect.Table(table);
    private string Query => $"SELECT id,project_id,sprint_id,plan_audit_id,plan_hash,outcome,members_json,evidence_json,bookkeeping_state,source_sha,deployment_candidate_id,created_at,updated_at,ready_for_deployment,delivery_note,source_evidence_json,abandonment_json,delivery_required,completion_policy_json,reconciliation_json FROM {T("sprint_completion")}";
    private static string Now() => IssueStore.DateFormatTime(DateTime.UtcNow);
    private static DbCommand Command(DbConnection connection, DbTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.AddParam(name, value ?? DBNull.Value);
        return command;
    }
    private async Task LockAsync(DbConnection connection, DbTransaction tx, CancellationToken ct)
    {
        using var command = Command(connection, tx, $"UPDATE {T("completion_lock")} SET value=value WHERE id=1");
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Completion lock unavailable.");
    }
    private static SprintCompletionRecord Read(DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : Convert.ToInt64(r.GetValue(3)),
        r.IsDBNull(4) ? null : r.GetString(4), Enum.Parse<EngineeringOutcome>(r.GetString(5)), r.GetString(6), r.GetString(7),
        r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
        IssueStore.ParseTime(r.GetString(11)), IssueStore.ParseTime(r.GetString(12)), Convert.ToInt32(r.GetValue(13)) != 0,
        r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), Convert.ToInt32(r.GetValue(17)) != 0, r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : r.GetString(19));
    private async Task<SprintCompletionRecord?> GetAsync(DbConnection connection, DbTransaction? tx, string id, CancellationToken ct)
    {
        using var command = Command(connection, tx, Query + " WHERE id=@id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }
    public async Task<SprintCompletionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        return await GetAsync(connection, null, id, ct);
    }
    public async Task<IReadOnlyList<SprintCompletionRecord>> ListVisibleAsync(int limit = 20, string? afterId = null, CancellationToken ct = default, bool waitingOnly = false)
        => await ListAsync(waitingOnly ? CompletionPlanningBarrier.Predicate(issues.Db.Dialect, includeLegacyIncomplete: true) : "1=1", limit, afterId, ct);

    public async Task<string?> GetDeploymentStatusAsync(string completionId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"SELECT d.status FROM {T("deployment")} d INNER JOIN {T("sprint_completion")} c ON c.deployment_candidate_id=d.id AND d.completion_id=c.id WHERE c.id=@id", ("@id", completionId));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public Task<IReadOnlyList<SprintCompletionRecord>> ListPlanningBlockersAsync(int limit = 20, string? afterId = null, CancellationToken ct = default)
        => ListAsync(CompletionPlanningBarrier.Predicate(issues.Db.Dialect), limit, afterId, ct);

    public async Task<IReadOnlyList<SprintCompletionRecord>> ListAwaitingDeploymentAsync(int limit = 50, string? afterId = null, CancellationToken ct = default)
        => await ListAsync("ready_for_deployment=1 AND delivery_required=1 AND deployment_candidate_id IS NULL AND " + DeploymentApprovalPredicate + " AND (completion_policy_json IS NULL OR " + CompletionPlanningBarrier.BookkeepingReadyPredicate(issues.Db.Dialect) + ")", limit, afterId, ct);
    public async Task<IReadOnlyList<SprintCompletionRecord>> ListAwaitingDeliveryOutcomeAsync(int limit = 50, string? afterId = null, CancellationToken ct = default)
        => await ListAsync("ready_for_deployment=1 AND deployment_candidate_id IS NOT NULL AND outcome='PendingEvidence'", limit, afterId, ct);
    public async Task<IReadOnlyList<SprintCompletionRecord>> ListPendingBookkeepingAsync(int limit = 50, string? afterId = null, CancellationToken ct = default)
        => await ListAsync($"abandonment_json IS NULL AND (bookkeeping_state <> 'Completed' OR EXISTS(SELECT 1 FROM {T("completion_triage")} pending WHERE pending.completion_id={T("sprint_completion")}.id AND pending.state IN ('Pending','Running','Decided')) OR EXISTS(SELECT 1 FROM {T("followup_draft")} d WHERE d.consumed_at IS NULL AND NOT EXISTS(SELECT 1 FROM {T("completion_draft_claim")} c WHERE c.draft_id=d.id) AND (d.sprint_id={T("sprint_completion")}.sprint_id OR EXISTS(SELECT 1 FROM {T("sprint_issue")} m WHERE m.sprint_id={T("sprint_completion")}.sprint_id AND m.issue_id=d.source_issue_id))))", limit, afterId, ct);
    private async Task<IReadOnlyList<SprintCompletionRecord>> ListAsync(string predicate, int limit, string? afterId, CancellationToken ct)
    {
        var d = issues.Db.Dialect;
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, Query.Replace("SELECT ", $"SELECT {d.Top(Math.Clamp(limit, 1, 100))}", StringComparison.Ordinal)
            + $" WHERE ({predicate})" + (afterId is null ? "" : " AND id>@after") + $" ORDER BY id{d.Limit(Math.Clamp(limit, 1, 100))}", ("@after", afterId));
        var result = new List<SprintCompletionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task<SprintCompletionRecord> BeginCompletionAsync(string sprintId, IReadOnlyList<IssueRecord> members,
        CompletionEvidence evidence, CancellationToken ct = default)
    {
        var id = "completion-" + sprintId;
        var deliveryRequired = evidence.DeliveryRequired;
        if (evidence.CompletionPolicy is { } policy && (policy.EnvironmentName is null && (policy.RequireApproval || policy.TargetJson is not null)
            || policy.EnvironmentName is not null && (string.IsNullOrWhiteSpace(policy.TargetJson) || !deliveryRequired)))
            throw new InvalidOperationException("Completion policy must identify a target for deployment and explicit approval.");
        if (deliveryRequired && evidence.Outcome == EngineeringOutcome.Satisfied)
            throw new InvalidOperationException("Required deployment must be observed before outcome satisfaction.");
        if (members.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw new InvalidOperationException("Completion requires distinct executable members.");
        if (members.Any(m => m.Status is not (IssueStatus.Completed or IssueStatus.Closed)))
            throw new InvalidOperationException("Unfinished, failed or blocked members prevent completion.");
        if (evidence.Outcome == EngineeringOutcome.Satisfied && (evidence.PlanAuditId is null || evidence.PlanHash is null))
            throw new InvalidOperationException("Legacy terminal work cannot claim verified outcome delivery.");
        if ((evidence.ReadyForDeployment || evidence.Outcome == EngineeringOutcome.Satisfied)
            && (members.Count == 0
                || evidence.PlanAuditId is null || evidence.Outcome is EngineeringOutcome.Incomplete or EngineeringOutcome.LegacyTerminalOnly))
            throw new InvalidOperationException("Verified member publication and a bound plan are required for delivery.");
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        var existing = await GetAsync(connection, tx, id, ct);
        if (existing is not null)
        {
            if (existing.PlanHash != evidence.PlanHash || existing.PlanAuditId != evidence.PlanAuditId)
                throw new InvalidOperationException("Completion plan identity cannot change.");
            return existing;
        }
        using (var captured = Command(connection, tx, $"SELECT completion_policy_json FROM {T("sprint")} WHERE id=@id AND status='Active'", ("@id", sprintId)))
        {
            await using var row = await captured.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new InvalidOperationException("Sprint is no longer active.");
            var saved = row.IsDBNull(0) ? null : row.GetString(0);
            if (saved is string json && json != (evidence.CompletionPolicy is null ? null : JsonSerializer.Serialize(evidence.CompletionPolicy)))
                throw new InvalidOperationException("Completion policy cannot differ from the policy captured at sprint activation.");
        }
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command(connection, tx, $"SELECT i.id,i.type,i.status,i.updated_at,i.metadata_json FROM {T("sprint_issue")} m LEFT JOIN {T("issue")} i ON i.id=m.issue_id WHERE m.sprint_id=@sprint", ("@sprint", sprintId)))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0)) throw new InvalidOperationException("Sprint contains a missing member.");
                if (AgentTaskTypes.IsContainer(reader.GetString(1)) || reader.GetString(1) == AgentTaskTypes.PrWatch) continue;
                var member = members.SingleOrDefault(m => m.Id == reader.GetString(0));
                if (member is null || reader.GetString(2) != member.Status.ToString()
                    || reader.GetString(3) != IssueStore.DateFormatTime(member.UpdatedAt) || reader.GetString(4) != member.MetadataJson)
                    throw new InvalidOperationException("Completion member evidence changed; reevaluate.");
                actual.Add(member.Id);
            }
        }
        if (!actual.SetEquals(members.Select(m => m.Id))) throw new InvalidOperationException("Completion membership changed.");
        if (evidence.PlanAuditId is { } auditId)
        {
            using var audit = Command(connection, tx, $"SELECT weights_json FROM {T("sprint_proposal_audit")} WHERE id=@audit AND committed_sprint_id=@sprint AND id=(SELECT MAX(id) FROM {T("sprint_proposal_audit")} WHERE committed_sprint_id=@sprint)", ("@audit", auditId), ("@sprint", sprintId));
            var weights = await audit.ExecuteScalarAsync(ct) as string;
            var plan = weights is null ? null : SprintPlanJson.ReadAuditPlan(weights);
            if (plan is null || plan.Hash != evidence.PlanHash || plan.ProjectId != issues.ProjectId
                || !actual.SetEquals(plan.Members.Select(m => m.TaskId)))
                throw new InvalidOperationException("Committed completion plan changed or does not cover the exact members.");
            if ((evidence.ReadyForDeployment || evidence.Outcome == EngineeringOutcome.Satisfied)
                && members.Any(m => !TaskDeliveryEvidence.IsImplementationVerified(m, plan)))
                throw new InvalidOperationException("Every implementation requires exact-plan trusted verification and independent delivery evidence.");
            if ((evidence.ReadyForDeployment || evidence.Outcome == EngineeringOutcome.Satisfied)
                && !await HasCanonicalIntegrationProofsAsync(connection, tx, members, plan, ct))
                throw new InvalidOperationException("Controller-owned integration verification is required for delivery.");
            deliveryRequired |= evidence.CompletionPolicy is null && plan.Criteria.Any(c => c.EvidenceKind is SprintEvidenceKind.DeploymentHealthy or SprintEvidenceKind.OperatorObserved);
            if (evidence.CompletionPolicy is null && evidence.Outcome == EngineeringOutcome.Satisfied && plan.Criteria.Any(c => c.EvidenceKind is SprintEvidenceKind.DeploymentHealthy or SprintEvidenceKind.OperatorObserved))
                throw new InvalidOperationException("Deployment criteria require the linked deployment outcome.");
        }
        else if (evidence.PlanHash is not null) throw new InvalidOperationException("Plan hash requires a bound audit.");
        var now = Now();
        using (var insert = Command(connection, tx, $"INSERT INTO {T("sprint_completion")}(id,project_id,sprint_id,plan_audit_id,plan_hash,outcome,members_json,evidence_json,bookkeeping_state,created_at,updated_at,ready_for_deployment,delivery_required,completion_policy_json) VALUES(@id,@project,@sprint,@audit,@hash,@outcome,@members,@evidence,'Pending',@now,@now,@ready,@deliveryRequired,@policy)",
            ("@id", id), ("@project", issues.ProjectId), ("@sprint", sprintId), ("@audit", evidence.PlanAuditId), ("@hash", evidence.PlanHash),
            ("@outcome", evidence.Outcome.ToString()), ("@members", JsonSerializer.Serialize(members)), ("@evidence", evidence.EvidenceJson), ("@now", now), ("@ready", evidence.ReadyForDeployment ? 1 : 0), ("@deliveryRequired", deliveryRequired ? 1 : 0), ("@policy", evidence.CompletionPolicy is null ? null : JsonSerializer.Serialize(evidence.CompletionPolicy))))
            await insert.ExecuteNonQueryAsync(ct);
        if (evidence.CompletionPolicy is null || evidence.Outcome == EngineeringOutcome.Satisfied)
        using (var finish = Command(connection, tx, $"UPDATE {T("sprint")} SET status='Completed',updated_at=@now WHERE id=@sprint AND status='Active'", ("@now", now), ("@sprint", sprintId)))
            if (await finish.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Sprint is no longer active.");
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task RecordSourceAsync(string id, string exactSha, string evidenceJson, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(exactSha, "^[a-fA-F0-9]{40}$")) throw new ArgumentException("Exact source SHA required.");
        using var parsed = JsonDocument.Parse(evidenceJson);
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        var current = await GetAsync(connection, tx, id, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (current.AbandonmentJson is not null)
            throw new InvalidOperationException("Completion is not eligible for source admission.");
        exactSha = exactSha.ToLowerInvariant();
        if (current.SourceSha is not null)
        {
            if (current.SourceSha != exactSha || current.SourceEvidenceJson != evidenceJson)
                throw new InvalidOperationException("Source evidence is immutable.");
            return;
        }
        using var command = Command(connection, tx, $"UPDATE {T("sprint_completion")} SET source_sha=@sha,source_evidence_json=@evidence,updated_at=@now WHERE id=@id",
            ("@sha", exactSha), ("@evidence", evidenceJson), ("@now", Now()), ("@id", id));
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<SprintPlan> GetBoundPlanAsync(SprintCompletionRecord receipt, CancellationToken ct = default)
    {
        if (receipt.ProjectId != issues.ProjectId || receipt.PlanAuditId is null)
            throw new InvalidOperationException("Completion lacks an owning-project plan.");
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null,
            $"SELECT weights_json FROM {T("sprint_proposal_audit")} WHERE id=@audit AND committed_sprint_id=@sprint",
            ("@audit", receipt.PlanAuditId), ("@sprint", receipt.SprintId));
        var plan = SprintPlanJson.ReadAuditPlan(await command.ExecuteScalarAsync(ct) as string ?? "{}");
        if (plan is null || plan.ProjectId != issues.ProjectId || plan.Hash != receipt.PlanHash)
            throw new InvalidOperationException("Completion plan evidence does not match its immutable binding.");
        return plan;
    }
    public Task BindDeploymentAsync(string id, string candidateId, CancellationToken ct = default)
        => SetBindingAsync(id, "deployment_candidate_id", candidateId, ct);
    private async Task SetBindingAsync(string id, string column, string value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Binding cannot be empty.");
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("sprint_completion")} SET {column}=@value,updated_at=@now WHERE id=@id AND abandonment_json IS NULL AND ({column} IS NULL OR {column}=@value)", ("@value", value), ("@now", Now()), ("@id", id));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Conflicting or missing completion binding.");
    }
    public async Task RecordDeliveryWaitAsync(string id, string? note, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("sprint_completion")} SET delivery_note=@note,updated_at=@now WHERE id=@id", ("@note", note), ("@now", Now()), ("@id", id));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Completion not found.");
    }

}
