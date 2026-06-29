using Microsoft.Data.Sqlite;

namespace PortHorizon.Agents.Core;

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

    public async Task<SprintRecord> CreateAsync(NewSprint spec, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var id = $"sprint-{Guid.NewGuid().ToString("N")[..10]}";
        var now = DateTime.UtcNow;
        if (spec.Status == SprintStatus.Active)
            await DeactivateOthersAsync(conn, tx, id, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = @"INSERT INTO sprint (id, name, goal, start_date, end_date, status, created_at, updated_at)
                VALUES ($id, $name, $goal, $start, $end, $status, $now, $now)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", spec.Name);
            cmd.Parameters.AddWithValue("$goal", spec.Goal);
            cmd.Parameters.AddWithValue("$start", IssueStore.DateFormatTime(spec.StartDate));
            cmd.Parameters.AddWithValue("$end", IssueStore.DateFormatTime(spec.EndDate));
            cmd.Parameters.AddWithValue("$status", spec.Status.ToString());
            cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new SprintRecord(id, spec.Name, spec.Goal, spec.StartDate, spec.EndDate, spec.Status, now, now);
    }

    public async Task<IReadOnlyList<SprintRecord>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = activeOnly
            ? "SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM sprint WHERE status='Active' ORDER BY start_date DESC"
            : "SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM sprint ORDER BY start_date DESC";
        var list = new List<SprintRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<SprintRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM sprint WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SprintRecord?> GetActiveAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, goal, start_date, end_date, status, created_at, updated_at FROM sprint WHERE status='Active' LIMIT 1";
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
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        if (merged.Status == SprintStatus.Active)
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await DeactivateOthersAsync(conn, tx, id, ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            ApplySprintUpdateCommand(cmd, merged, now);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            await using var cmd = conn.CreateCommand();
            ApplySprintUpdateCommand(cmd, merged, now);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return merged with { UpdatedAt = now };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sprint WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SprintRecord> SetActiveAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await DeactivateOthersAsync(conn, tx, id, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "UPDATE sprint SET status='Active', updated_at=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(DateTime.UtcNow));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task AddIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR IGNORE INTO sprint_issue (sprint_id, issue_id, added_at)
            VALUES ($sid, $iid, $now)";
        cmd.Parameters.AddWithValue("$sid", sprintId);
        cmd.Parameters.AddWithValue("$iid", issueId);
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveIssueAsync(string sprintId, string issueId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sprint_issue WHERE sprint_id = $sid AND issue_id = $iid";
        cmd.Parameters.AddWithValue("$sid", sprintId);
        cmd.Parameters.AddWithValue("$iid", issueId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetIssueIdsAsync(string sprintId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT issue_id FROM sprint_issue WHERE sprint_id = $sid ORDER BY added_at";
        cmd.Parameters.AddWithValue("$sid", sprintId);
        var ids = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetString(0));
        return ids;
    }

    private static async Task DeactivateOthersAsync(SqliteConnection conn, SqliteTransaction tx, string keepId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE sprint SET status='Archived', updated_at=$now WHERE status='Active' AND id != $keep";
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$keep", keepId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void ApplySprintUpdateCommand(SqliteCommand cmd, SprintRecord s, DateTime now)
    {
        cmd.CommandText = @"UPDATE sprint SET name=$name, goal=$goal, start_date=$start, end_date=$end, status=$status, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$name", s.Name);
        cmd.Parameters.AddWithValue("$goal", s.Goal);
        cmd.Parameters.AddWithValue("$start", IssueStore.DateFormatTime(s.StartDate));
        cmd.Parameters.AddWithValue("$end", IssueStore.DateFormatTime(s.EndDate));
        cmd.Parameters.AddWithValue("$status", s.Status.ToString());
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
    }

    private static SprintRecord Read(SqliteDataReader rd) => new(
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



