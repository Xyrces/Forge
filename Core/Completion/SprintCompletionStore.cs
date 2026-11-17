using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Core.Db;

namespace Forge.Core.Completion;

/// <summary>Project-local completion authority. Events are hints; receipts survive missed events.</summary>
public sealed partial class SprintCompletionStore(IssueStore issues)
{
    private string T(string table) => issues.Db.Dialect.Table(table);
    private string Query => $"SELECT id,project_id,sprint_id,plan_audit_id,plan_hash,outcome,members_json,evidence_json,bookkeeping_state,source_sha,deployment_candidate_id,created_at,updated_at,ready_for_deployment,delivery_note,source_evidence_json FROM {T("sprint_completion")}";
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
        r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15));
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
    public async Task<IReadOnlyList<SprintCompletionRecord>> ListAwaitingDeploymentAsync(int limit = 50, string? afterId = null, CancellationToken ct = default)
        => await ListAsync("ready_for_deployment=1 AND deployment_candidate_id IS NULL", limit, afterId, ct);
    public async Task<IReadOnlyList<SprintCompletionRecord>> ListPendingBookkeepingAsync(int limit = 50, string? afterId = null, CancellationToken ct = default)
        => await ListAsync("bookkeeping_state <> 'Completed'", limit, afterId, ct);
    private async Task<IReadOnlyList<SprintCompletionRecord>> ListAsync(string predicate, int limit, string? afterId, CancellationToken ct)
    {
        var d = issues.Db.Dialect;
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, Query.Replace("SELECT ", $"SELECT {d.Top(Math.Clamp(limit, 1, 100))}", StringComparison.Ordinal)
            + $" WHERE {predicate}" + (afterId is null ? "" : " AND id>@after") + $" ORDER BY id{d.Limit(Math.Clamp(limit, 1, 100))}", ("@after", afterId));
        var result = new List<SprintCompletionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task<SprintCompletionRecord> BeginCompletionAsync(string sprintId, IReadOnlyList<IssueRecord> members,
        CompletionEvidence evidence, CancellationToken ct = default)
    {
        var id = "completion-" + sprintId;
        if (members.Count == 0 || members.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw new InvalidOperationException("Completion requires distinct executable members; empty or missing work is not delivery evidence.");
        if (members.Any(m => m.Status is not (IssueStatus.Completed or IssueStatus.Closed)))
            throw new InvalidOperationException("Unfinished, failed or blocked members prevent completion.");
        if (evidence.Outcome == EngineeringOutcome.Satisfied && (evidence.PlanAuditId is null || evidence.PlanHash is null))
            throw new InvalidOperationException("Legacy terminal work cannot claim verified outcome delivery.");
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
        var now = Now();
        using (var insert = Command(connection, tx, $"INSERT INTO {T("sprint_completion")}(id,project_id,sprint_id,plan_audit_id,plan_hash,outcome,members_json,evidence_json,bookkeeping_state,created_at,updated_at,ready_for_deployment) VALUES(@id,@project,@sprint,@audit,@hash,@outcome,@members,@evidence,'Pending',@now,@now,@ready)",
            ("@id", id), ("@project", issues.ProjectId), ("@sprint", sprintId), ("@audit", evidence.PlanAuditId), ("@hash", evidence.PlanHash),
            ("@outcome", evidence.Outcome.ToString()), ("@members", JsonSerializer.Serialize(members)), ("@evidence", evidence.EvidenceJson), ("@now", now), ("@ready", evidence.ReadyForDeployment ? 1 : 0)))
            await insert.ExecuteNonQueryAsync(ct);
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
    public Task BindDeploymentAsync(string id, string candidateId, CancellationToken ct = default)
        => SetBindingAsync(id, "deployment_candidate_id", candidateId, ct);
    private async Task SetBindingAsync(string id, string column, string value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Binding cannot be empty.");
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("sprint_completion")} SET {column}=@value,updated_at=@now WHERE id=@id AND ({column} IS NULL OR {column}=@value)", ("@value", value), ("@now", Now()), ("@id", id));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Conflicting or missing completion binding.");
    }
    public async Task RecordDeliveryWaitAsync(string id, string? note, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"UPDATE {T("sprint_completion")} SET delivery_note=@note,updated_at=@now WHERE id=@id", ("@note", note), ("@now", Now()), ("@id", id));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Completion not found.");
    }
}
