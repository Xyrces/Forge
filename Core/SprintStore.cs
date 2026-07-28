using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

public enum SprintStatus { Active, Completed, Archived }

public sealed record SprintRecord(
    string Id, string Name, string Goal,
    DateTime StartDate, DateTime EndDate,
    SprintStatus Status,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record NewSprint(
    string Name, string Goal,
    DateTime StartDate, DateTime EndDate,
    SprintStatus Status = SprintStatus.Active);

public interface ISprintStore
{
    Task<SprintRecord> CreateAsync(NewSprint spec, CancellationToken ct = default);
    Task<IReadOnlyList<SprintRecord>> ListAsync(bool activeOnly, CancellationToken ct = default);
    Task<SprintRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<SprintRecord?> GetActiveAsync(CancellationToken ct = default);
    Task<SprintRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<SprintRecord> SetActiveAsync(string id, CancellationToken ct = default);
    Task AddIssueAsync(string sprintId, string issueId, CancellationToken ct = default);
    Task RemoveIssueAsync(string sprintId, string issueId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetIssueIdsAsync(string sprintId, CancellationToken ct = default);
}

public sealed class SprintStore : ISprintStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public SprintStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public async Task<SprintRecord> CreateAsync(NewSprint spec, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = $"sprint-{Guid.NewGuid().ToString("N")[..10]}";
        var now = DateTime.UtcNow;
        if (spec.Status == SprintStatus.Active)
            await DeactivateOthersAsync(conn, tx, id, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("sprint")} (id, name, goal, start_date, end_date, status, created_at, updated_at)
                VALUES (@id, @name, @goal, @start, @end, @status, @now, @now)
                """;
            cmd.AddParam("@id", id);
            cmd.AddParam("@name", spec.Name);
            cmd.AddParam("@goal", spec.Goal);
            cmd.AddParam("@start", IssueStore.DateFormatTime(spec.StartDate));
            cmd.AddParam("@end", IssueStore.DateFormatTime(spec.EndDate));
            cmd.AddParam("@status", spec.Status.ToString());
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new SprintRecord(id, spec.Name, spec.Goal, spec.StartDate, spec.EndDate, spec.Status, now, now);
    }

    public async Task<IReadOnlyList<SprintRecord>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = activeOnly
            ? $"SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} WHERE status='Active' ORDER BY start_date DESC"
            : $"SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} ORDER BY start_date DESC";
        var list = new List<SprintRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<SprintRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SprintRecord?> GetActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var d = _issues.Db.Dialect;
        cmd.CommandText = $"SELECT {d.Top(1)}id, name, goal, start_date, end_date, status, created_at, updated_at FROM {T("sprint")} WHERE status='Active'{d.Limit(1)}";
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SprintRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Sprint {id} not found");
        var merged = existing with
        {
            Name = fields.TryGetValue("name", out var nm) && nm is string s1 ? s1 : existing.Name,
            Goal = fields.TryGetValue("goal", out var gl) && gl is string s2 ? s2 : existing.Goal,
            StartDate = fields.TryGetValue("startDate", out var sd) ? Convert.ToDateTime(sd) : existing.StartDate,
            EndDate = fields.TryGetValue("endDate", out var ed) ? Convert.ToDateTime(ed) : existing.EndDate,
            Status = fields.TryGetValue("status", out var st) ? Enum.Parse<SprintStatus>(st?.ToString() ?? "Active") : existing.Status,
        };
        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);
        if (merged.Status == SprintStatus.Active)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await DeactivateOthersAsync(conn, tx, id, ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            ApplySprintUpdateCommand(cmd, merged, now);
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            ApplySprintUpdateCommand(cmd, merged, now);
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return merged with { UpdatedAt = now };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("sprint")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SprintRecord> SetActiveAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await DeactivateOthersAsync(conn, tx, id, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""UPDATE {T("sprint")} SET status='Active', updated_at=@now WHERE id=@id""";
            cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task AddIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                IF NOT EXISTS (SELECT 1 FROM {T("sprint_issue")} WHERE sprint_id = @sid AND issue_id = @iid)
                INSERT INTO {T("sprint_issue")} (sprint_id, issue_id, added_at) VALUES (@sid, @iid, @now);
                """
            : """
                INSERT OR IGNORE INTO sprint_issue (sprint_id, issue_id, added_at)
                VALUES (@sid, @iid, @now)
                """;
        cmd.AddParam("@sid", sprintId);
        cmd.AddParam("@iid", issueId);
        cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("sprint_issue")} WHERE sprint_id = @sid AND issue_id = @iid";
        cmd.AddParam("@sid", sprintId);
        cmd.AddParam("@iid", issueId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetIssueIdsAsync(string sprintId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT issue_id FROM {T("sprint_issue")} WHERE sprint_id = @sid ORDER BY added_at";
        cmd.AddParam("@sid", sprintId);
        var ids = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetString(0));
        return ids;
    }

    private async Task DeactivateOthersAsync(DbConnection conn, DbTransaction tx, string keepId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""UPDATE {T("sprint")} SET status='Archived', updated_at=@now WHERE status='Active' AND id != @keep""";
        cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
        cmd.AddParam("@keep", keepId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void ApplySprintUpdateCommand(DbCommand cmd, SprintRecord s, DateTime now)
    {
        cmd.CommandText = $"""UPDATE {T("sprint")} SET name=@name, goal=@goal, start_date=@start, end_date=@end, status=@status, updated_at=@now WHERE id=@id""";
        cmd.AddParam("@name", s.Name);
        cmd.AddParam("@goal", s.Goal);
        cmd.AddParam("@start", IssueStore.DateFormatTime(s.StartDate));
        cmd.AddParam("@end", IssueStore.DateFormatTime(s.EndDate));
        cmd.AddParam("@status", s.Status.ToString());
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
    }

    private static SprintRecord Read(DbDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        Goal: rd.GetString(2),
        StartDate: IssueStore.ParseTime(rd.GetString(3)),
        EndDate: IssueStore.ParseTime(rd.GetString(4)),
        Status: Enum.Parse<SprintStatus>(rd.GetString(5)),
        CreatedAt: IssueStore.ParseTime(rd.GetString(6)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(7)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}



