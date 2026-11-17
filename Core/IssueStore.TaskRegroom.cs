using System.Data;
using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record TaskRegroomSnapshot(DateTime UpdatedAt, string BindingHash);
public sealed record TaskRegroomAvailability(bool Eligible, string? Reason, TaskRegroomSnapshot? Snapshot);
public sealed record TaskRegroomRequest(DateTime ExpectedUpdatedAt, string ExpectedBindingHash, string Reason);

public sealed partial class IssueStore
{
    public async Task<TaskRegroomAvailability> PreviewRegroomAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await Db.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var row = await RequireRegroomableAsync(connection, tx, id, ct);
            await tx.CommitAsync(ct);
            return new(true, null, new(row.UpdatedAt, SprintPlanJson.Hash(SprintPlanJson.ReadBinding(row)!)));
        }
        catch (InvalidOperationException ex) { return new(false, ex.Message, null); }
    }

    public async Task RequestRegroomAsync(string id, TaskRegroomRequest request, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000
            || string.IsNullOrWhiteSpace(actor) || actor.Length > 200)
            throw new ArgumentException("An operator identity and reason (at most 2000 characters) are required.");
        await using var connection = await Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await RequireRegroomableAsync(connection, tx, id, ct);
        if (row.UpdatedAt != request.ExpectedUpdatedAt || SprintPlanJson.Hash(SprintPlanJson.ReadBinding(row)!) != request.ExpectedBindingHash)
            throw new InvalidOperationException("Task grooming changed; reload the re-groom preview.");
        var metadata = MergeAndSerializeMetadata(row.MetadataJson, new Dictionary<string, object>
        {
            ["groomed"] = false, ["groomedAt"] = null!, ["groomNote"] = null!, ["groomRunId"] = null!,
            ["regroomReason"] = request.Reason
        });
        using var update = connection.CreateCommand(); update.Transaction = tx;
        update.CommandText = $"UPDATE {T("issue")} SET metadata_json=@meta,updated_at=@now WHERE id=@id AND status='Pending' AND assignee IS NULL AND parent_issue_id IS NULL AND dispatch_checkpoint IS NULL AND updated_at=@expected AND metadata_json=@old";
        update.AddParam("@meta", metadata); update.AddParam("@now", DateFormatTime(DateTime.UtcNow));
        update.AddParam("@id", id); update.AddParam("@expected", DateFormatTime(row.UpdatedAt)); update.AddParam("@old", row.MetadataJson);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Task changed during re-groom request.");
        await InsertEventAsync(connection, tx, id, "operator_regroom_requested",
            SprintPlanJson.Serialize(new { actor, request.Reason, previousMetadata = row.MetadataJson, previousUpdatedAt = row.UpdatedAt }), ct);
        await tx.CommitAsync(ct);
    }

    private async Task<IssueRecord> RequireRegroomableAsync(DbConnection connection, DbTransaction tx, string id, CancellationToken ct)
    {
        DbCommand Command(string sql)
        {
            var cmd = connection.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.AddParam("@id", id); return cmd;
        }
        IssueRecord row;
        using (var read = Command($"SELECT id,short_id,type,title,description,status,priority,assignee,created_at,updated_at,closed_at,metadata_json,parent_issue_id,dispatch_checkpoint,checkpoint_at,recovery_attempts FROM {T("issue")} WHERE id=@id"))
        {
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Task not found.");
            row = ReadIssue(r);
        }
        if (row.Status != IssueStatus.Pending || row.ParentIssueId is not null || row.Assignee is not null
            || AgentTaskTypes.IsContainer(row.Type) || row.Type == AgentTaskTypes.PrWatch || row.ClosedAt is not null
            || row.DispatchCheckpoint is not null || row.CheckpointAt is not null || row.RecoveryAttempts != 0
            || !string.Equals(row.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only groomed, unclaimed, parentless Pending tasks can be re-groomed.");
        if (new[] { "dispatchId", "resumeDispatchId", "branch", "worktreePath", "prNumber", "planGate", "state", "agentRunId", "agentSessionId", "reviewSha", "mergedSha" }
            .Any(key => !string.IsNullOrEmpty(row.GetMetadata(key))))
            throw new InvalidOperationException("Execution or publication evidence prevents re-grooming.");
        using (var history = Command($"SELECT COUNT(*) FROM {T("issue_event")} WHERE issue_id=@id AND NOT(kind IN('created','grooming_metadata','dep_added','dep_removed','priority_change','operator_regroom_requested') OR (kind='status_change' AND detail='Pending->Pending'))"))
            if (Convert.ToInt64(await history.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Task has prior execution/lifecycle history.");
        using (var runs = Command($"SELECT COUNT(*) FROM {T("agent_run")} WHERE task_id=@id"))
            if (Convert.ToInt64(await runs.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Task has prior agent execution.");
        var auditMember = Db.Provider == ForgeDbProvider.SqlServer
            ? "SELECT 1 FROM OPENJSON(a.selected_task_ids_json) j WHERE j.[value]=@id"
            : "SELECT 1 FROM json_each(a.selected_task_ids_json) j WHERE j.value=@id";
        using (var audits = Command($"SELECT COUNT(*) FROM {T("sprint_proposal_audit")} a WHERE a.committed_sprint_id IS NOT NULL AND EXISTS({auditMember})"))
            if (Convert.ToInt64(await audits.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("Task belongs to a committed sprint plan and cannot be re-groomed.");
        var binding = SprintPlanJson.ReadBinding(row) ?? throw new InvalidOperationException("Task has no readable approved outcome mapping.");
        SprintOutcomeAuthority.ValidateShape(id, binding);
        var authority = new SprintOutcomeAuthority(this);
        foreach (var scope in binding.Criteria.Select(c => c.Scope).Append(binding.Authority).Distinct())
            await authority.ValidateAsync(connection, tx, scope, ct);
        return row;
    }
}
