using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core.Completion;

public sealed partial class SprintCompletionStore
{
    public async Task<IReadOnlyList<FollowUpDraft>> ListUnclaimedDraftsAsync(string completionId, CancellationToken ct = default)
    {
        var completion = await GetAsync(completionId, ct) ?? throw new InvalidOperationException("Completion not found.");
        var d = issues.Db.Dialect;
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"SELECT {d.Top(100)}f.id FROM {T("followup_draft")} f WHERE f.consumed_at IS NULL AND NOT EXISTS(SELECT 1 FROM {T("completion_draft_claim")} c WHERE c.draft_id=f.id) AND (f.sprint_id=@sprint OR EXISTS(SELECT 1 FROM {T("sprint_issue")} m WHERE m.sprint_id=@sprint AND m.issue_id=f.source_issue_id)) ORDER BY f.id{d.Limit(100)}", ("@sprint", completion.SprintId));
        var ids = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(Convert.ToInt64(reader.GetValue(0)));
        var drafts = new FollowUpDraftStore(issues);
        var result = new List<FollowUpDraft>();
        foreach (var id in ids) result.Add(await drafts.GetAsync(id, ct) ?? throw new InvalidOperationException("Draft disappeared."));
        return result;
    }

    public async Task MarkBookkeepingCompleteAsync(string completionId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("sprint_completion")} SET bookkeeping_state='Completed',updated_at=@now WHERE id=@id AND NOT EXISTS(SELECT 1 FROM {T("completion_triage")} b WHERE b.completion_id=@id AND b.state <> 'Completed') AND NOT EXISTS(SELECT 1 FROM {T("followup_draft")} d WHERE d.consumed_at IS NULL AND NOT EXISTS(SELECT 1 FROM {T("completion_draft_claim")} c WHERE c.draft_id=d.id) AND (d.sprint_id={T("sprint_completion")}.sprint_id OR EXISTS(SELECT 1 FROM {T("sprint_issue")} m WHERE m.sprint_id={T("sprint_completion")}.sprint_id AND m.issue_id=d.source_issue_id)))", ("@now", Now()), ("@id", completionId));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task RecordMaterializationFailureAsync(string batchId, string error, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("completion_triage")} SET error=@error,updated_at=@now WHERE id=@id AND state='Decided'", ("@error", error), ("@now", Now()), ("@id", batchId));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordTriageFailureAsync(string batchId, string error, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("completion_triage")} SET state='NeedsReview',error=@error,updated_at=@now WHERE id=@id AND state='Running'", ("@error", error), ("@now", Now()), ("@id", batchId));
        await command.ExecuteNonQueryAsync(ct);
    }
    private static CompletionTriageBatch ReadBatch(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), IssueStore.ParseTime(reader.GetString(6)));
    private string BatchQuery => $"SELECT id,completion_id,state,sources_json,decision_json,error,updated_at FROM {T("completion_triage")}";
    private async Task<CompletionTriageBatch?> GetBatchAsync(DbConnection connection, DbTransaction? tx, string id, CancellationToken ct)
    {
        using var command = Command(connection, tx, BatchQuery + " WHERE id=@id", ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadBatch(reader) : null;
    }
    public async Task<IReadOnlyList<CompletionTriageBatch>> ListBatchesAsync(string completionId, CancellationToken ct = default,
        int limit = 100, string? afterId = null, bool actionableOnly = false)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        var d = issues.Db.Dialect;
        var take = Math.Clamp(limit, 1, 100);
        using var command = Command(connection, null, BatchQuery.Replace("SELECT ", $"SELECT {d.Top(take)}", StringComparison.Ordinal)
            + " WHERE completion_id=@completion" + (afterId is null ? "" : " AND id>@after")
            + (actionableOnly ? " AND state IN ('Pending','Decided')" : "")
            + $" ORDER BY id{d.Limit(take)}", ("@completion", completionId), ("@after", afterId));
        var result = new List<CompletionTriageBatch>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadBatch(reader));
        return result;
    }

    public async Task<CompletionTriageBatch> ClaimBatchAsync(string completionId, IReadOnlyList<FollowUpDraft> drafts, CancellationToken ct = default)
    {
        if (drafts.Count is < 1 or > 100 || drafts.Select(d => d.Id).Distinct().Count() != drafts.Count)
            throw new InvalidOperationException("A batch requires 1–100 distinct source drafts.");
        var ids = drafts.Select(d => d.Id).Order().ToArray();
        var id = "triage-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(completionId + ":" + string.Join(",", ids))));
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        var previous = await GetBatchAsync(connection, tx, id, ct);
        if (previous is not null) return previous;
        var completion = await GetAsync(connection, tx, completionId, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (completion.AbandonmentJson is not null) throw new InvalidOperationException("Completion was explicitly abandoned.");
        var members = JsonSerializer.Deserialize<IssueRecord[]>(completion.MembersJson)!.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (draft.SprintId != completion.SprintId && !members.Contains(draft.SourceIssueId))
                throw new InvalidOperationException("Draft does not belong to this completion.");
            using var check = Command(connection, tx, $"SELECT title,description,source_issue_id,source_role,priority,COALESCE(task_type,''),consumed_at,COALESCE(sprint_id,''),COALESCE(blocks_issue_id,'') FROM {T("followup_draft")} WHERE id=@id", ("@id", draft.Id));
            await using (var reader = await check.ExecuteReaderAsync(ct))
                if (!await reader.ReadAsync(ct) || reader.GetString(0) != draft.Title || reader.GetString(1) != draft.Description
                    || reader.GetString(2) != draft.SourceIssueId || reader.GetString(3) != draft.SourceRole
                    || Convert.ToInt32(reader.GetValue(4)) != draft.Priority || reader.GetString(5) != (draft.TaskType ?? "") || !reader.IsDBNull(6)
                    || reader.GetString(7) != (draft.SprintId ?? "") || reader.GetString(8) != (draft.BlocksIssueId ?? ""))
                    throw new InvalidOperationException("Draft changed or was already consumed.");
            using var claim = Command(connection, tx, $"INSERT INTO {T("completion_draft_claim")}(draft_id,batch_id) VALUES(@draft,@batch)", ("@draft", draft.Id), ("@batch", id));
            await claim.ExecuteNonQueryAsync(ct);
        }
        using var insert = Command(connection, tx, $"INSERT INTO {T("completion_triage")}(id,completion_id,state,sources_json,updated_at) VALUES(@id,@completion,'Pending',@sources,@now)",
            ("@id", id), ("@completion", completionId), ("@sources", JsonSerializer.Serialize(drafts.OrderBy(d => d.Id))), ("@now", Now()));
        await insert.ExecuteNonQueryAsync(ct);
        using var reopen = Command(connection, tx, $"UPDATE {T("sprint_completion")} SET bookkeeping_state='Pending',updated_at=@now WHERE id=@id", ("@now", Now()), ("@id", completionId));
        await reopen.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetBatchAsync(connection, null, id, ct))!;
    }
    public async Task<bool> TryBeginTriageAsync(string batchId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        using var command = Command(connection, tx, $"UPDATE {T("completion_triage")} SET state='Running',updated_at=@now WHERE id=@id AND state='Pending' AND EXISTS(SELECT 1 FROM {T("sprint_completion")} c WHERE c.id={T("completion_triage")}.completion_id AND c.abandonment_json IS NULL)", ("@now", Now()), ("@id", batchId));
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return changed;
    }
    public async Task SaveDecisionAsync(string batchId, FollowUpTriageDecision decision, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        var batch = await GetBatchAsync(connection, tx, batchId, ct) ?? throw new InvalidOperationException("Batch not found.");
        var completion = await GetAsync(connection, tx, batch.CompletionId, ct);
        if (completion?.AbandonmentJson is not null) throw new InvalidOperationException("Completion was explicitly abandoned.");
        var json = JsonSerializer.Serialize(decision);
        if (batch.DecisionJson is not null)
        {
            if (batch.DecisionJson != json) throw new InvalidOperationException("Recorded triage decision is immutable.");
            return;
        }
        if (batch.State != "Running") throw new InvalidOperationException("Triage is not running.");
        var sources = JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)!;
        var selected = decision.Items.SelectMany(i => i.SourceDraftIds).ToArray();
        if (decision.Items.Any(i => i.Action is not ("create" or "merge" or "epic" or "discard") || i.SourceDraftIds.Count == 0)
            || selected.Length != selected.Distinct().Count() || !selected.Order().SequenceEqual(sources.Select(s => s.Id).Order()))
            throw new InvalidOperationException("Triage must cover every source exactly once with a recognized action.");
        using var command = Command(connection, tx, $"UPDATE {T("completion_triage")} SET state='Decided',decision_json=@decision,updated_at=@now WHERE id=@id", ("@decision", json), ("@now", Now()), ("@id", batchId));
        await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<string?> MaterializeItemAsync(string batchId, int itemIndex, string? proposalId = null, CancellationToken ct = default)
        => (await MaterializeItemWithResultAsync(batchId, itemIndex, proposalId, ct)).TargetId;

    public async Task<CompletionMaterializationResult> MaterializeItemWithResultAsync(string batchId, int itemIndex, string? proposalId = null, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        var batch = await GetBatchAsync(connection, tx, batchId, ct) ?? throw new InvalidOperationException("Batch not found.");
        var completion = await GetAsync(connection, tx, batch.CompletionId, ct);
        if (completion?.AbandonmentJson is not null) throw new InvalidOperationException("Completion was explicitly abandoned.");
        if (batch.State is not ("Decided" or "Completed") || batch.DecisionJson is null) throw new InvalidOperationException("Recorded decision required.");
        var decision = JsonSerializer.Deserialize<FollowUpTriageDecision>(batch.DecisionJson)!;
        if (itemIndex < 0 || itemIndex >= decision.Items.Count) throw new ArgumentOutOfRangeException(nameof(itemIndex));
        var item = decision.Items[itemIndex];
        var itemKey = batchId + ":" + itemIndex;
        using (var prior = Command(connection, tx, $"SELECT item_key,target_id FROM {T("completion_draft_claim")} WHERE draft_id=@id AND batch_id=@batch", ("@id", item.SourceDraftIds[0]), ("@batch", batchId)))
        {
            await using var reader = await prior.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Draft claim missing.");
            if (!reader.IsDBNull(0))
            {
                if (reader.GetString(0) != itemKey) throw new InvalidOperationException("Conflicting materialization.");
                var existingTarget = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (item.Action == "epic" && existingTarget != proposalId)
                    throw new InvalidOperationException("Proposal identity cannot change on replay.");
                return new(existingTarget, false);
            }
        }
        var source = JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)!.Where(d => item.SourceDraftIds.Contains(d.Id)).ToArray();
        IssueRecord? created = null;
        string? target;
        if (item.Action == "epic") target = proposalId ?? throw new InvalidOperationException("Create the stable unapproved proposal before acknowledging its sources.");
        else if (item.Action == "discard") target = null;
        else
        {
            created = await issues.CreateInTransactionAsync(connection, tx, new NewIssue(
                source.Select(s => s.TaskType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "task",
                item.Title ?? source[0].Title, item.Description ?? string.Join("\n\n", source.Select(s => s.Description)),
                Priority: item.Priority ?? source.Min(s => s.Priority), Metadata: new Dictionary<string, object>
                {
                    ["followUpOf"] = source[0].SourceIssueId, ["fromDraft"] = string.Join(",", item.SourceDraftIds),
                    ["completionItem"] = itemKey, ["source"] = source[0].SourceRole
                }), ct);
            target = created.Id;
        }
        foreach (var id in item.SourceDraftIds)
        {
            using var consume = Command(connection, tx, $"UPDATE {T("followup_draft")} SET consumed_at=@now,disposition=@action,disposition_detail=@target WHERE id=@id AND consumed_at IS NULL",
                ("@now", Now()), ("@action", item.Action switch { "epic" => "proposal", "merge" => "merged", "discard" => "discarded", _ => "materialized" }), ("@target", target ?? item.Reason), ("@id", id));
            if (await consume.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Draft already consumed outside its batch.");
            using var claim = Command(connection, tx, $"UPDATE {T("completion_draft_claim")} SET item_key=@key,target_id=@target WHERE draft_id=@id AND batch_id=@batch AND item_key IS NULL",
                ("@key", itemKey), ("@target", target), ("@id", id), ("@batch", batchId));
            if (await claim.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Draft claim changed.");
        }
        using var finish = Command(connection, tx, $"UPDATE {T("completion_triage")} SET state='Completed',error=NULL,updated_at=@now WHERE id=@batch AND NOT EXISTS(SELECT 1 FROM {T("completion_draft_claim")} WHERE batch_id=@batch AND item_key IS NULL)", ("@now", Now()), ("@batch", batchId));
        await finish.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        if (created is not null) await issues.PublishCreatedAsync(created, ct);
        return new(target, item.Action != "discard");
    }
    public async Task RecoverRunningAsync(CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("completion_triage")} SET state='NeedsReview',error='Triage interrupted; previous paid request may have completed. Explicit retry required.',updated_at=@now WHERE state='Running'", ("@now", Now()));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> RetryTriageAsync(string batchId, CancellationToken ct = default, string? completionId = null)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, tx, ct);
        using var command = Command(connection, tx, $"UPDATE {T("completion_triage")} SET state='Pending',error=NULL,updated_at=@now WHERE id=@id AND state='NeedsReview' AND (@completion IS NULL OR completion_id=@completion) AND EXISTS(SELECT 1 FROM {T("sprint_completion")} c WHERE c.id={T("completion_triage")}.completion_id AND c.abandonment_json IS NULL)", ("@now", Now()), ("@id", batchId), ("@completion", completionId));
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return changed;
    }
}
