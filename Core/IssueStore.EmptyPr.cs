using System.Data;
using System.Data.Common;
using System.Text.Json;
using Forge.Core.Completion;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record RetiredEmptyPr(int Number, string HeadSha, string BaseSha, string BaseBranch,
    string Branch, DateTimeOffset ObservedAt);

public sealed partial class IssueStore
{
    private async Task<SprintPlan?> ReadActiveTaskPlanAsync(DbConnection connection, DbTransaction? transaction,
        string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT a.weights_json FROM {T("sprint_proposal_audit")} a JOIN {T("sprint")} s ON s.id=a.committed_sprint_id JOIN {T("sprint_issue")} m ON m.sprint_id=s.id WHERE m.issue_id=@id AND s.status='Active' AND a.id=(SELECT MAX(latest.id) FROM {T("sprint_proposal_audit")} latest WHERE latest.committed_sprint_id=s.id)";
        command.AddParam("@id", id);
        return SprintPlanJson.ReadAuditPlan(await command.ExecuteScalarAsync(ct) as string ?? "{}");
    }

    public async Task<bool> CanVerifyEmptyPrAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await Db.OpenAsync(ct);
        var plan = await ReadActiveTaskPlanAsync(connection, null, id, ct);
        return SupportsExistingVerification(plan, id);
    }

    private bool SupportsExistingVerification(SprintPlan? plan, string id)
    {
        if (plan is null || plan.ProjectId != ProjectId) return false;
        var member = plan.Members.SingleOrDefault(m => m.TaskId == id);
        if (member is null) return false;
        var engineering = plan.Criteria.Where(c => member.CriterionIds.Contains(c.Id)
            && c.EvidenceKind is SprintEvidenceKind.MergedAndVerified or SprintEvidenceKind.ImplementationVerified).ToArray();
        return engineering.Length > 0 && engineering.All(c => c.EvidenceKind == SprintEvidenceKind.ImplementationVerified);
    }

    /// <summary>After observed PR closure and joined worker ownership, retain the task's scope and request fresh verification.</summary>
    public async Task RetireEmptyPrAsync(IssueRecord expected, RetiredEmptyPr observation, CancellationToken ct = default)
    {
        if (observation.Number <= 0 || observation.HeadSha.Length != 40 || observation.BaseSha.Length != 40
            || !observation.HeadSha.All(char.IsAsciiHexDigit) || !observation.BaseSha.All(char.IsAsciiHexDigit)
            || string.IsNullOrWhiteSpace(observation.BaseBranch) || string.IsNullOrWhiteSpace(observation.Branch))
            throw new InvalidOperationException("Exact closed, unmerged PR source identity is required.");
        await using var connection = await Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = $"SELECT id,short_id,type,title,description,status,priority,assignee,created_at,updated_at,closed_at,metadata_json,parent_issue_id,dispatch_checkpoint,checkpoint_at,recovery_attempts FROM {T("issue")} WHERE id=@id";
        read.AddParam("@id", expected.Id);
        IssueRecord current;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Task not found.");
            current = ReadIssue(reader);
        }
        if (current.Status is not (IssueStatus.Pending or IssueStatus.InProgress or IssueStatus.Blocked)
            || current.UpdatedAt != expected.UpdatedAt || current.MetadataJson != expected.MetadataJson
            || current.GetMetadata("prNumber") != observation.Number.ToString()
            || current.GetMetadata("branch") != observation.Branch)
            throw new InvalidOperationException("Task changed before empty PR retirement.");
        var plan = await ReadActiveTaskPlanAsync(connection, tx, current.Id, ct);
        var binding = SprintPlanJson.ReadBinding(current);
        if (!SupportsExistingVerification(plan, current.Id) || binding is null
            || plan!.Members.Single(m => m.TaskId == current.Id).BindingHash != SprintPlanJson.Hash(binding))
            throw new InvalidOperationException("The exact active plan must permit independently verified existing implementation.");
        foreach (var scope in plan.Criteria.Select(c => c.Scope).Distinct())
            await new SprintOutcomeAuthority(this).ValidateAsync(connection, tx, scope, ct);
        var suffix = $"-verify-{observation.Number}-{observation.HeadSha[..8]}";
        var additions = TaskStateMachine.ObservedMetadata(current, TaskEvent.EmptyPrRetired, DateTime.UtcNow);
        // The immutable event retains all retired associations; only the live pointers are cleared.
        using var oldMetadata = JsonDocument.Parse(current.MetadataJson);
        foreach (var property in oldMetadata.RootElement.EnumerateObject())
            if (property.Name.StartsWith("review", StringComparison.Ordinal)
                || property.Name.StartsWith("qa", StringComparison.Ordinal)
                || property.Name.StartsWith("rework", StringComparison.Ordinal)) additions[property.Name] = null!;
        foreach (var key in new[] { "prNumber", "prOpenedAt", "branchSha", "worktreePath", "trustedPublication", "observedMerge",
            "resumeDispatchId", "dispatchId", "planGate", "parkedForSha", "blockedKind", "lastError", "lastErrorAt", "noProgressAttempts" })
            additions[key] = null!;
        additions["branch"] = observation.Branch + suffix;
        additions["verificationCheckoutKey"] = current.Id + suffix;
        additions["existingVerificationRequest"] = JsonSerializer.Serialize(observation);
        var metadata = MergeAndSerializeMetadata(current.MetadataJson, additions);
        await ImplementationEvidenceFormat.ValidateJsonAsync(connection, tx, Db.Dialect, metadata, ct);
        using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = $"UPDATE {T("issue")} SET status='Pending',closed_at=NULL,assignee=NULL,dispatch_checkpoint=NULL,checkpoint_at=NULL,updated_at=@now,metadata_json=@meta WHERE id=@id AND updated_at=@expected AND metadata_json=@old";
        update.AddParam("@id", current.Id); update.AddParam("@now", DateFormatTime(DateTime.UtcNow));
        update.AddParam("@expected", DateFormatTime(expected.UpdatedAt)); update.AddParam("@old", expected.MetadataJson);
        update.AddParam("@meta", metadata);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Task changed during PR retirement.");
        await InsertEventAsync(connection, tx, current.Id, "empty_pr_retired",
            JsonSerializer.Serialize(new { observation, planHash = plan.Hash, priorMetadata = current.MetadataJson }), ct);
        await InsertEventAsync(connection, tx, current.Id, "status_change", $"{current.Status}->Pending (existing implementation requires verification)", ct);
        await tx.CommitAsync(ct);
    }

    private async Task<IssueRecord> ClosePositiveDispositionAsync(string id, CloseDisposition disposition, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = $"SELECT id,short_id,type,title,description,status,priority,assignee,created_at,updated_at,closed_at,metadata_json,parent_issue_id,dispatch_checkpoint,checkpoint_at,recovery_attempts FROM {T("issue")} WHERE id=@id";
        read.AddParam("@id", id);
        IssueRecord task;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Task not found.");
            task = ReadIssue(reader);
        }
        using var audit = connection.CreateCommand();
        audit.Transaction = tx;
        audit.CommandText = $"SELECT a.weights_json FROM {T("sprint_proposal_audit")} a JOIN {T("sprint_issue")} m ON m.sprint_id=a.committed_sprint_id WHERE m.issue_id=@id ORDER BY a.id DESC";
        audit.AddParam("@id", id);
        await using (var reader = await audit.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var plan = SprintPlanJson.ReadAuditPlan(reader.GetString(0));
                if (plan is null || plan.Members.All(m => m.TaskId != id)) continue;
                if (TaskDeliveryEvidence.IsImplementationVerified(task, plan)) return task; // Keep canonical Completed proof, never relabel it Closed.
                throw new InvalidOperationException("A committed sprint member requires exact implementation evidence; prose or another completed task cannot close its acceptance obligation.");
            }
        }
        if (disposition.Kind == CloseDispositionKind.DuplicateDelivered)
        {
            using var target = connection.CreateCommand(); target.Transaction = tx;
            target.CommandText = $"SELECT status FROM {T("issue")} WHERE id=@id";
            target.AddParam("@id", disposition.ReferenceIssueId);
            if (disposition.ReferenceIssueId == id || await target.ExecuteScalarAsync(ct) as string != "Completed")
                throw new InvalidOperationException("Duplicate closure requires a different completed issue in this project.");
        }
        var metadata = MergeAndSerializeMetadata(task.MetadataJson, new Dictionary<string, object>
        {
            ["closeDisposition"] = disposition.Kind.ToString(), ["closeEvidence"] = disposition.Evidence ?? "",
            ["closeReferenceIssueId"] = disposition.ReferenceIssueId ?? "", ["closeReason"] = disposition.Reason
        });
        await ImplementationEvidenceFormat.ValidateJsonAsync(connection, tx, Db.Dialect, metadata, ct);
        using var update = connection.CreateCommand(); update.Transaction = tx;
        update.CommandText = $"UPDATE {T("issue")} SET status='Closed',closed_at=@now,updated_at=@now,metadata_json=@meta,assignee=NULL,dispatch_checkpoint=NULL,checkpoint_at=NULL WHERE id=@id";
        update.AddParam("@id", id); update.AddParam("@now", DateFormatTime(DateTime.UtcNow)); update.AddParam("@meta", metadata);
        await update.ExecuteNonQueryAsync(ct);
        await InsertEventAsync(connection, tx, id, "status_change", $"{task.Status}->Closed ({disposition.Kind}: {disposition.Reason})", ct);
        await tx.CommitAsync(ct);
        await PublishLifecycleTransitionAsync(task, metadata, ct);
        return (await GetAsync(id, ct))!;
    }
}
