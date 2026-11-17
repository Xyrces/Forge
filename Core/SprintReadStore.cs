using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record SprintHistoryPage(SprintRecord? Active, long Total, IReadOnlyList<SprintRecord> History, string? NextCursor);
public sealed record SprintMemberRead(IssueRecord Issue, bool Boosted, long BlockerCount, IReadOnlyList<string> BlockedBy);
public sealed record SprintBoardPage(SprintRecord? Sprint, long Total, long Done, IReadOnlyList<SprintMemberRead> Members, string? NextCursor);
public sealed record SprintQueuePreview(long Total, long BlockedTotal, IReadOnlyList<SprintMemberRead> Ready, IReadOnlyList<SprintMemberRead> Blocked);

/// <summary>Read-only, bounded projections. No orchestration policy or mutations.</summary>
public sealed class SprintReadStore(IDbConnectionFactory db)
{
    private string T(string name) => db.Dialect.Table(name);
    private const string Engineering = "LOWER(i.type) NOT IN ('epic','story','pr-watch')";
    private string Membership => $"FROM {T("sprint_issue")} si JOIN {T("issue")} i ON i.id=si.issue_id WHERE si.sprint_id=@sid AND {Engineering}";
    private string Blocked => $"EXISTS (SELECT 1 FROM {T("issue_dep")} dep JOIN {T("issue")} b ON b.id=dep.blocker_id WHERE dep.blocked_id=i.id AND dep.kind='blocks' AND b.status NOT IN ('Completed','Closed'))";
    private string Boost => $"CASE WHEN EXISTS (SELECT 1 FROM {T("issue_dep")} dep JOIN {T("issue")} b ON b.id=dep.blocked_id WHERE dep.blocker_id=i.id AND dep.kind='blocks' AND b.status NOT IN ('Completed','Closed')) THEN 0 ELSE 1 END";
    private const string SprintColumns = "id,name,goal,start_date,end_date,status,created_at,updated_at";
    private sealed record Cursor(string Scope, string Date, string Id);
    private static Cursor? Decode(string? value, string scope)
    {
        if (value is null) return null;
        try
        {
            if (value.Length > 2048) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Scope != scope || string.IsNullOrEmpty(cursor.Id)
                || !DateTime.TryParse(cursor.Date, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new FormatException();
            return cursor;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new ArgumentException("Invalid pagination cursor for this selection.", nameof(value), ex); }
    }
    private static string Encode(string scope, DateTime date, string id) => Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new Cursor(scope, IssueStore.DateFormatTime(date), id)));
    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in args) command.AddParam(name, value);
        return command;
    }
    private static SprintRecord ReadSprint(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        IssueStore.ParseTime(r.GetString(3)), IssueStore.ParseTime(r.GetString(4)), Enum.Parse<SprintStatus>(r.GetString(5)),
        IssueStore.ParseTime(r.GetString(6)), IssueStore.ParseTime(r.GetString(7)));

    public async Task<SprintHistoryPage> HistoryAsync(string projectId, int limit = 20, string? cursor = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var after = Decode(cursor, projectId + ":history");
        await using var connection = await db.OpenAsync(ct);
        SprintRecord? active;
        using (var command = Command(connection, $"SELECT {db.Dialect.Top(1)}{SprintColumns} FROM {T("sprint")} WHERE status='Active'{db.Dialect.Limit(1)}"))
        {
            await using var r = await command.ExecuteReaderAsync(ct); active = await r.ReadAsync(ct) ? ReadSprint(r) : null;
        }
        long total;
        using (var command = Command(connection, $"SELECT COUNT(*) FROM {T("sprint")}")) total = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        var history = new List<SprintRecord>();
        // Goals are intentionally omitted from history payloads and reads.
        var columns = SprintColumns.Replace("goal", "'' AS goal", StringComparison.Ordinal);
        using (var command = Command(connection, $"SELECT {db.Dialect.Top(limit + 1)}{columns} FROM {T("sprint")} WHERE status<>'Active'"
            + (after is null ? "" : " AND (start_date<@date OR (start_date=@date AND id<@id))")
            + $" ORDER BY start_date DESC,id DESC{db.Dialect.Limit(limit + 1)}", ("@date", after?.Date), ("@id", after?.Id)))
        {
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) history.Add(ReadSprint(r));
        }
        var next = history.Count > limit ? Encode(projectId + ":history", history[limit - 1].StartDate, history[limit - 1].Id) : null;
        return new(active is null ? null : active with { Goal = "" }, total, history.Take(limit).ToArray(), next);
    }

    public async Task<SprintBoardPage> BoardAsync(string projectId, string? sprintId = null, int limit = 100, string? cursor = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await db.OpenAsync(ct);
        SprintRecord? sprint;
        using (var command = Command(connection, $"SELECT {db.Dialect.Top(1)}{SprintColumns} FROM {T("sprint")}"
            + (sprintId is null ? " ORDER BY CASE WHEN status='Active' THEN 0 ELSE 1 END,start_date DESC,id DESC" : " WHERE id=@sid")
            + db.Dialect.Limit(1), ("@sid", sprintId)))
        {
            await using var r = await command.ExecuteReaderAsync(ct); sprint = await r.ReadAsync(ct) ? ReadSprint(r) : null;
        }
        if (sprint is null) return new(null, 0, 0, [], null);
        var scope = projectId + ":" + sprint.Id;
        var after = Decode(cursor, scope);
        long total, done;
        using (var command = Command(connection, $"SELECT COUNT(*),COALESCE(SUM(CASE WHEN i.status IN ('Completed','Closed') THEN 1 ELSE 0 END),0) {Membership}", ("@sid", sprint.Id)))
        {
            await using var r = await command.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
            total = Convert.ToInt64(r.GetValue(0)); done = Convert.ToInt64(r.GetValue(1));
        }
        var rows = await MembersAsync(connection, sprint.Id, limit + 1,
            after is null ? "" : " AND (i.created_at>@date OR (i.created_at=@date AND i.id>@id))",
            "i.created_at,i.id", after, ct);
        var next = rows.Count > limit ? Encode(scope, rows[limit - 1].Issue.CreatedAt, rows[limit - 1].Issue.Id) : null;
        var page = rows.Take(limit).ToArray();
        return new(sprint, total, done, await WithBlockersAsync(connection, page, ct), next);
    }

    public async Task<SprintQueuePreview> QueueAsync(string sprintId, int limit = 8, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        await using var connection = await db.OpenAsync(ct);
        long total, blocked;
        // Count the two groups without SQL Server's prohibition on aggregates over subqueries.
        using (var command = Command(connection, $"SELECT COUNT(*) {Membership} AND i.status='Pending' AND NOT {Blocked}", ("@sid", sprintId)))
            total = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        using (var command = Command(connection, $"SELECT COUNT(*) {Membership} AND i.status='Pending' AND {Blocked}", ("@sid", sprintId)))
            blocked = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        var ready = await MembersAsync(connection, sprintId, limit, $" AND i.status='Pending' AND NOT {Blocked}", $"{Boost},i.priority,i.created_at,i.id", null, ct);
        var blockedRows = await MembersAsync(connection, sprintId, limit, $" AND i.status='Pending' AND {Blocked}", "i.priority,i.created_at,i.id", null, ct);
        return new(total, blocked, ready, await WithBlockersAsync(connection, blockedRows, ct));
    }

    private async Task<List<SprintMemberRead>> MembersAsync(DbConnection connection, string sprintId, int limit, string predicate, string order, Cursor? after, CancellationToken ct)
    {
        using var command = Command(connection, $"SELECT {db.Dialect.Top(limit)}i.id,i.type,i.title,i.status,i.priority,i.assignee,i.created_at,i.updated_at,i.metadata_json,i.parent_issue_id,{Boost} AS boost {Membership}{predicate} ORDER BY {order}{db.Dialect.Limit(limit)}",
            ("@sid", sprintId), ("@date", after?.Date), ("@id", after?.Id));
        var rows = new List<SprintMemberRead>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var issue = new IssueRecord(r.GetString(0), "", r.GetString(1), r.GetString(2), null,
                Enum.Parse<IssueStatus>(r.GetString(3)), Convert.ToInt32(r.GetValue(4)), r.IsDBNull(5) ? null : r.GetString(5),
                IssueStore.ParseTime(r.GetString(6)), IssueStore.ParseTime(r.GetString(7)), null,
                r.IsDBNull(8) ? "{}" : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));
            rows.Add(new(issue, Convert.ToInt32(r.GetValue(10)) == 0, 0, []));
        }
        return rows;
    }

    private async Task<IReadOnlyList<SprintMemberRead>> WithBlockersAsync(DbConnection connection, IReadOnlyList<SprintMemberRead> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return rows;
        var args = rows.Select((row, index) => ($"@b{index}", (object?)row.Issue.Id)).ToArray();
        using var command = Command(connection, $"""
            SELECT blocked_id,blocker_id,total FROM (
                SELECT dep.blocked_id,dep.blocker_id,COUNT(*) OVER(PARTITION BY dep.blocked_id) AS total,
                       ROW_NUMBER() OVER(PARTITION BY dep.blocked_id ORDER BY dep.blocker_id) AS rn
                FROM {T("issue_dep")} dep JOIN {T("issue")} b ON b.id=dep.blocker_id
                WHERE dep.kind='blocks' AND b.status NOT IN ('Completed','Closed')
                    AND dep.blocked_id IN ({string.Join(',', args.Select(a => a.Item1))})
            ) bounded WHERE rn<=5 ORDER BY blocked_id,blocker_id
            """, args);
        var blockers = new Dictionary<string, (long Total, List<string> Ids)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (!blockers.TryGetValue(id, out var entry)) entry = (Convert.ToInt64(reader.GetValue(2)), []);
            entry.Ids.Add(reader.GetString(1)); blockers[id] = entry;
        }
        return rows.Select(row => blockers.TryGetValue(row.Issue.Id, out var entry)
            ? row with { BlockerCount = entry.Total, BlockedBy = entry.Ids } : row).ToArray();
    }
}
