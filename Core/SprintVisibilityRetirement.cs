using System.Data;
using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class SprintStore
{
    /// <summary>Retire rejected visibility links, never committed engineering membership or task history.</summary>
    public async Task<int> RetireClosedVisibilityMembersAsync(string sprintId, CancellationToken ct = default)
    {
        await using var connection = await _issues.Db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using (var sprint = Command($"SELECT status FROM {T("sprint")} WHERE id=@sprint"))
            if (await sprint.ExecuteScalarAsync(ct) is not string status || status != "Active") return 0;

        // Read the complete bounded audit range under the transaction. An amendment cannot
        // commit a new member between this authority check and the link deletion.
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var auditCount = 0;
        using (var audits = Command($"SELECT {_issues.Db.Dialect.Top(1001)}weights_json FROM {T("sprint_proposal_audit")} WHERE committed_sprint_id=@sprint ORDER BY id{_issues.Db.Dialect.Limit(1001)}"))
        await using (var reader = await audits.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (++auditCount > 1000) throw new InvalidOperationException("Visibility reconciliation exceeds the sprint audit bound; investigate the history.");
                var plan = SprintPlanJson.ReadAuditPlan(reader.GetString(0));
                if (plan is null || plan.ProjectId != _issues.ProjectId)
                    throw new InvalidOperationException("Visibility reconciliation requires intact project-owned committed plans.");
                committed.UnionWith(plan.Members.Select(m => m.TaskId));
            }
        }
        if (auditCount == 0) return 0;

        var candidates = new List<IssueRecord>();
        using (var rows = Command($"SELECT {_issues.Db.Dialect.Top(101)}i.id,i.short_id,i.type,i.title,i.description,i.status,i.priority,i.assignee,i.created_at,i.updated_at,i.closed_at,i.metadata_json,i.parent_issue_id,i.dispatch_checkpoint,i.checkpoint_at,i.recovery_attempts FROM {T("sprint_issue")} m JOIN {T("issue")} i ON i.id=m.issue_id WHERE m.sprint_id=@sprint AND i.status='Closed' ORDER BY i.id{_issues.Db.Dialect.Limit(101)}"))
        await using (var reader = await rows.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) candidates.Add(IssueStore.ReadIssue(reader));
        if (candidates.Count > 100) throw new InvalidOperationException("Visibility reconciliation exceeds the closed-member bound; investigate membership.");
        var retired = 0;
        foreach (var row in candidates)
        {
            if (committed.Contains(row.Id) || row.ParentIssueId is not null || AgentTaskTypes.IsContainer(row.Type)
                || row.Assignee is not null || row.DispatchCheckpoint is not null || row.CheckpointAt is not null || row.RecoveryAttempts != 0
                || new[] { "prNumber", "branch", "worktreePath", "dispatchId", "planGate" }.Any(key => !string.IsNullOrEmpty(row.GetMetadata(key)))) continue;
            var source = row.GetMetadata("followUpOf");
            var groomRejected = !string.IsNullOrWhiteSpace(row.GetMetadata("groomCloseReason")) && !string.IsNullOrWhiteSpace(row.GetMetadata("groomRunId"));
            var cancelled = row.GetMetadata("closeDisposition") == nameof(CloseDispositionKind.Cancelled)
                && !string.IsNullOrWhiteSpace(row.GetMetadata("closeReason"));
            if (source is null || !committed.Contains(source) || (!groomRejected && !cancelled)) continue;
            using (var edge = Command($"SELECT COUNT(*) FROM {T("issue_dep")} WHERE blocker_id=@issue AND blocked_id=@source AND kind='blocks'"))
            {
                edge.AddParam("@issue", row.Id); edge.AddParam("@source", source);
                if (Convert.ToInt64(await edge.ExecuteScalarAsync(ct)) == 0) continue;
            }
            using (var remove = Command($"DELETE FROM {T("sprint_issue")} WHERE sprint_id=@sprint AND issue_id=@issue"))
            {
                remove.AddParam("@issue", row.Id);
                if (await remove.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Visibility membership changed; retry reconciliation.");
            }
            await _issues.RecordVisibilityRetirementAsync(connection, transaction, row.Id, sprintId, ct);
            retired++;
        }
        await transaction.CommitAsync(ct);
        return retired;

        DbCommand Command(string sql)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            command.AddParam("@sprint", sprintId); return command;
        }
    }
}

public sealed partial class IssueStore
{
    internal Task RecordVisibilityRetirementAsync(DbConnection connection, DbTransaction transaction,
        string issueId, string sprintId, CancellationToken ct)
        => InsertEventAsync(connection, transaction, issueId, "sprint_visibility_retired",
            $"Removed rejected uncommitted visibility link from {sprintId}; task disposition and dependency history retained, no delivery claimed.", ct);
}
