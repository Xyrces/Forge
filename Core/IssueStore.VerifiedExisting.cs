using System.Data;
using System.Text.Json;
using Forge.Core.Completion;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class IssueStore
{
    /// <summary>Controller-owned evidence admission; external verification/review occurs before this short transaction.</summary>
    public async Task CompleteVerifiedExistingAsync(string id, DateTime expectedUpdatedAt, string expectedMetadataJson,
        VerifiedExistingImplementationEvidence evidence, CancellationToken ct = default)
    {
        if (evidence.ProjectId != _projectId || evidence.TaskId != id)
            throw new InvalidOperationException("Existing implementation evidence belongs to another task or project.");
        var serialized = JsonSerializer.Serialize(evidence);
        await using var connection = await Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var read = connection.CreateCommand(); read.Transaction = tx;
        read.CommandText = $"SELECT id,short_id,type,title,description,status,priority,assignee,created_at,updated_at,closed_at,metadata_json,parent_issue_id,dispatch_checkpoint,checkpoint_at,recovery_attempts FROM {T("issue")} WHERE id=@id";
        read.AddParam("@id", id);
        IssueRecord current;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Task not found.");
            current = ReadIssue(reader);
        }
        if (current.Status == IssueStatus.Completed && current.GetMetadata(ExistingImplementationEvidence.MetadataKey) == serialized)
            return; // Exact replay does not rewrite historical evidence or emit another event.
        if (current.Status != IssueStatus.InProgress || current.UpdatedAt != expectedUpdatedAt || current.MetadataJson != expectedMetadataJson)
            throw new InvalidOperationException("Task changed during independent existing-implementation verification.");
        using var audit = connection.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = $"SELECT a.weights_json FROM {T("sprint_proposal_audit")} a JOIN {T("sprint")} s ON s.id=a.committed_sprint_id JOIN {T("sprint_issue")} m ON m.sprint_id=s.id WHERE m.issue_id=@id AND s.status='Active' AND a.id=(SELECT MAX(latest.id) FROM {T("sprint_proposal_audit")} latest WHERE latest.committed_sprint_id=s.id)";
        audit.AddParam("@id", id);
        var plan = SprintPlanJson.ReadAuditPlan(await audit.ExecuteScalarAsync(ct) as string ?? "{}");
        var metadata = MergeAndSerializeMetadata(current.MetadataJson, new Dictionary<string, object>
        { [ExistingImplementationEvidence.MetadataKey] = serialized, ["lastError"] = null!, ["lastErrorAt"] = null! });
        if (plan is null || plan.ProjectId != _projectId
            || !ExistingImplementationEvidence.TryGet(current with { Status = IssueStatus.Completed, MetadataJson = metadata }, plan, out _))
            throw new InvalidOperationException("Exact active plan, criteria and independent verification evidence are required.");
        var binding = SprintPlanJson.ReadBinding(current);
        var member = plan.Members.Single(m => m.TaskId == id);
        if (binding is null || member.BindingHash is null || member.BindingHash != SprintPlanJson.Hash(binding))
            throw new InvalidOperationException("Task scope mapping no longer matches the committed plan.");
        foreach (var scope in evidence.Criteria.Select(c => c.Scope).Distinct())
            await new SprintOutcomeAuthority(this).ValidateAsync(connection, tx, scope, ct);
        var now = DateFormatTime(DateTime.UtcNow);
        using var update = connection.CreateCommand(); update.Transaction = tx;
        update.CommandText = $"UPDATE {T("issue")} SET status='Completed',closed_at=@now,updated_at=@now,metadata_json=@metadata WHERE id=@id AND status='InProgress' AND updated_at=@expected AND metadata_json=@old";
        update.AddParam("@now", now); update.AddParam("@metadata", metadata); update.AddParam("@id", id);
        update.AddParam("@expected", DateFormatTime(expectedUpdatedAt)); update.AddParam("@old", expectedMetadataJson);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Task changed before evidence admission.");
        await InsertEventAsync(connection, tx, id, "existing_implementation_verified", serialized, ct);
        await InsertEventAsync(connection, tx, id, "status_change", "InProgress->Completed (independently verified existing implementation)", ct);
        await tx.CommitAsync(ct);
    }
}
