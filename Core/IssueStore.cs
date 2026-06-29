using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace PortHorizon.Agents.Core;

public enum IssueStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Blocked,
    Closed
}

public sealed record IssueRecord(
    string Id,
    string ShortId,
    string Type,
    string Title,
    string? Description,
    IssueStatus Status,
    int Priority,
    string? Assignee,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    string MetadataJson)
{
    public string? GetMetadata(string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(MetadataJson) ? "{}" : MetadataJson);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record NewIssue(
    string Type,
    string Title,
    string? Description = null,
    int Priority = 2,
    string? Assignee = null,
    IReadOnlyDictionary<string, object>? Metadata = null
);

public sealed record IssueFilter
{
    public IssueStatus? Status { get; init; }
    public string? Assignee { get; init; }
    public string? Type { get; init; }
}

public interface IIssueStore
{
    Task<IssueRecord> CreateAsync(NewIssue spec, CancellationToken ct = default);
    Task<IReadOnlyList<IssueRecord>> ListAsync(IssueFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, CancellationToken ct = default);
    Task<IssueRecord?> ClaimAsync(string id, string assignee, CancellationToken ct = default);
    Task<IssueRecord> TransitionAsync(string id, IssueStatus to, string? error, IDictionary<string, object>? metadata = null, CancellationToken ct = default);
    Task<IssueRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IssueEventRecord> AddEventAsync(string id, string kind, string? detail = null, CancellationToken ct = default);
}

/// <summary>
/// SQLite-backed issue store. WAL mode + busy_timeout make the store safe
/// for concurrent writers (orchestrator + enqueue CLI + dashboard reads)
/// without losing updates the way the previous state.json did.
///
/// Implements Phase 1 of docs/embedded-issues.md: just the issue/event/memory
/// tables + the four core methods. Stays additive to (not replacing) StateStore,
/// which still owns orchestrator settings + heartbeats.
/// </summary>
public sealed class IssueStore : IIssueStore, IAsyncDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly string _connectionString;
    private readonly string _dbPath;

    public IssueStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS issue (
                id            TEXT PRIMARY KEY,
                short_id      TEXT NOT NULL,
                type          TEXT NOT NULL,
                title         TEXT NOT NULL,
                description   TEXT,
                status        TEXT NOT NULL,
                priority      INTEGER NOT NULL DEFAULT 2,
                assignee      TEXT,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                closed_at     TEXT,
                metadata_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS ix_issue_status     ON issue(status);
            CREATE INDEX IF NOT EXISTS ix_issue_assignee   ON issue(assignee);
            CREATE INDEX IF NOT EXISTS ix_issue_updated_at ON issue(updated_at);
            CREATE INDEX IF NOT EXISTS ix_issue_type_short ON issue(type, short_id);

            CREATE TABLE IF NOT EXISTS issue_event (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                issue_id    TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
                ts          TEXT NOT NULL,
                actor       TEXT,
                kind        TEXT NOT NULL,
                detail      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_issue_event_issue ON issue_event(issue_id, ts);

            CREATE TABLE IF NOT EXISTS issue_seq (
                type TEXT PRIMARY KEY,
                next_short INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS schema_version (
                version    INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_version(version, applied_at)
            VALUES ($version, $now);
            """;
        cmd.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString(DateFormat));
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public async Task<IssueRecord> CreateAsync(NewIssue spec, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Per-type monotonic short_id; transaction keeps it race-safe.
        var shortId = await NextShortIdAsync(conn, tx, spec.Type);
        var shortIdStr = shortId.ToString();
        var id = $"{spec.Type}-{shortIdStr}";
        var now = DateTime.UtcNow;
        var metadataJson = SerializeMetadata(spec.Metadata);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO issue (id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, metadata_json)
                VALUES ($id, $short, $type, $title, $desc, $status, $pri, $assignee, $now, $now, $meta);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$short", shortId);
            cmd.Parameters.AddWithValue("$type", spec.Type);
            cmd.Parameters.AddWithValue("$title", spec.Title);
            cmd.Parameters.AddWithValue("$desc", (object?)spec.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", "Pending");
            cmd.Parameters.AddWithValue("$pri", spec.Priority);
            cmd.Parameters.AddWithValue("$assignee", (object?)spec.Assignee ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$meta", metadataJson);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertEventAsync(conn, tx, id, "created", spec.Description, ct);
        await tx.CommitAsync(ct);
        return new IssueRecord(id, shortIdStr, spec.Type, spec.Title, spec.Description,
            IssueStatus.Pending, spec.Priority, spec.Assignee, now, now, null, metadataJson);
    }

    public async Task<IReadOnlyList<IssueRecord>> ListAsync(IssueFilter filter, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = "SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json FROM issue WHERE 1=1";
        if (filter.Status is not null) sql += " AND status = $status";
        if (filter.Assignee is not null) sql += " AND assignee = $assignee";
        if (filter.Type is not null) sql += " AND type = $type";
        sql += " ORDER BY created_at";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (filter.Status is not null) cmd.Parameters.AddWithValue("$status", filter.Status.ToString());
        if (filter.Assignee is not null) cmd.Parameters.AddWithValue("$assignee", filter.Assignee);
        if (filter.Type is not null) cmd.Parameters.AddWithValue("$type", filter.Type);

        var list = new List<IssueRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(ReadIssue(rd));
        return list;
    }

    public async Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, CancellationToken ct = default)
    {
        // No dependency graph yet (phase 1) — every Pending issue is ready.
        var pending = await ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct);
        return limit > 0 ? pending.Take(limit).ToList() : pending;
    }

    public async Task<IssueRecord?> ClaimAsync(string id, string assignee, CancellationToken ct = default)
    {
        // Atomic transition: only succeeds if status is currently 'Pending'.
        // Concurrency-safe via SQLite's WAL + the single-writer guarantee per row.
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var now = DateTime.UtcNow;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE issue
                SET status = 'InProgress', assignee = $assignee, updated_at = $now
                WHERE id = $id AND status = 'Pending'
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$assignee", assignee);
            cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) return null; // already claimed or not Pending
        }

        await InsertEventAsync(conn, null, id, "claimed", $"assignee={assignee}", ct);
        return await GetAsync(id, ct);
    }

    public async Task<IssueRecord> TransitionAsync(
        string id,
        IssueStatus to,
        string? error,
        IDictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var now = DateTime.UtcNow;
        var isTerminal = to is IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Blocked or IssueStatus.Closed;
        var current = await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Issue {id} not found");

        var metadataJson = metadata is { Count: > 0 }
            ? MergeAndSerializeMetadata(current.MetadataJson, metadata)
            : current.MetadataJson;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = isTerminal
                ? """UPDATE issue SET status=$to, updated_at=$now, closed_at=$now, metadata_json=$meta WHERE id=$id"""
                : """UPDATE issue SET status=$to, updated_at=$now, metadata_json=$meta WHERE id=$id""";
            cmd.Parameters.AddWithValue("$to", to.ToString());
            cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$meta", metadataJson);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertEventAsync(conn, null, id, "status_change",
            $"{current.Status}->{to}{(error is null ? "" : $" err={error}")}", ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<IssueRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json
            FROM issue WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadIssue(rd) : null;
    }

    public async Task<IssueEventRecord> AddEventAsync(string id, string kind, string? detail = null, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await InsertEventAsync(conn, null, id, kind, detail, ct);
    }

    private static async Task<int> NextShortIdAsync(SqliteConnection conn, SqliteTransaction tx, string type)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO issue_seq(type, next_short) VALUES($t, 2)
            ON CONFLICT(type) DO UPDATE SET next_short = next_short + 1
            RETURNING next_short - 1
            """;
        cmd.Parameters.AddWithValue("$t", type);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task<IssueEventRecord> InsertEventAsync(
        SqliteConnection conn, SqliteTransaction? tx, string issueId, string kind, string? detail, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO issue_event(issue_id, ts, actor, kind, detail)
            VALUES($id, $ts, $actor, $kind, $detail);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", issueId);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$actor", "orchestrator");
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return new IssueEventRecord(id, issueId, DateTime.UtcNow, "orchestrator", kind, detail);
    }

    private static IssueRecord ReadIssue(SqliteDataReader rd)
        => new(
            Id: rd.GetString(0),
            ShortId: rd.GetString(1),
            Type: rd.GetString(2),
            Title: rd.GetString(3),
            Description: rd.IsDBNull(4) ? null : rd.GetString(4),
            Status: Enum.Parse<IssueStatus>(rd.GetString(5)),
            Priority: rd.GetInt32(6),
            Assignee: rd.IsDBNull(7) ? null : rd.GetString(7),
            CreatedAt: ParseDate(rd.GetString(8)),
            UpdatedAt: ParseDate(rd.GetString(9)),
            ClosedAt: rd.IsDBNull(10) ? null : ParseDate(rd.GetString(10)),
            MetadataJson: rd.GetString(11));

    private static DateTime ParseDate(string s) => DateTime.ParseExact(s, DateFormat, System.Globalization.CultureInfo.InvariantCulture);

    private static string SerializeMetadata(IReadOnlyDictionary<string, object>? src)
    {
        var dict = src is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(src);
        return JsonSerializer.Serialize(dict);
    }

    private static string MergeAndSerializeMetadata(string existing, IDictionary<string, object> additions)
    {
        Dictionary<string, object> merged;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(existing) ? "{}" : existing);
            merged = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                merged[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        }
        catch
        {
            merged = new Dictionary<string, object>();
        }
        foreach (var kv in additions)
            merged[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(merged);
    }

    public async ValueTask DisposeAsync()
    {
        // Connection pooling handles cleanup; nothing to do explicitly.
        await ValueTask.CompletedTask;
    }

    public void Dispose() { /* pooled connections */ }
}

public sealed record IssueEventRecord(
    long Id,
    string IssueId,
    DateTime Timestamp,
    string Actor,
    string Kind,
    string? Detail);
