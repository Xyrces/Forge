using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// P5.2 — context_handoff lineage table. Records which artifact
/// ids each agent (per task) actually read via
/// <see cref="ArtifactReadTool"/>. Used for closed-loop
/// debugging: "the Artist didn't see the wireframe" → look at
/// this table to see whether the Artist's read_artifact call
/// happened.
///
/// <para>
/// Schema v12 — added by P5.2. Migration runs in
/// <see cref="IssueStore.InitializeSchema"/> (which is the
/// shared schema bootstrap that the rest of the stores
/// rely on). For tests, the simplest path is to construct
/// <see cref="ContextHandoffStore"/> with the same dbPath and
/// call <see cref="EnsureCreatedAsync"/>; the production
/// schema bootstrap covers the operator's runtime.
/// </para>
/// </summary>
public sealed class ContextHandoffStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public ContextHandoffStore(string dbPath)
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
    }

    public string DbPath => _dbPath;

    /// <summary>
    /// Idempotent. Creates the context_handoff table if it
    /// doesn't exist. Production wiring goes through the shared
    /// IssueStore.InitializeSchema migration; this method is for
    /// tests + first-boot bootstrap.
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS context_handoff (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ts              TEXT NOT NULL,
                task_id         TEXT NOT NULL,
                from_role       TEXT NOT NULL,    -- 'designer' | 'artist' | 'groomer' | 'core_dev' | ...
                to_role         TEXT NOT NULL,
                artifact_id     TEXT NOT NULL,
                artifact_kind   TEXT NOT NULL,    -- 'design' | 'spec' | 'art'
                consumed        INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_context_handoff_task
                ON context_handoff(task_id, ts);
            CREATE INDEX IF NOT EXISTS ix_context_handoff_artifact
                ON context_handoff(artifact_id, ts);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Log a read_artifact call. Called from
    /// <see cref="ArtifactReadTool.ReadArtifactAsync"/>. The
    /// `from_role` and `to_role` are optional — the producer
    /// might be the spec body itself (no agent) and the
    /// consumer might not have a role name at the time of the
    /// read. We pass empty strings in that case; the column is
    /// not null.
    /// </summary>
    public async Task LogReadAsync(
        string artifactId,
        string kind,
        CancellationToken ct = default,
        string taskId = "",
        string fromRole = "",
        string toRole = "",
        bool consumed = true)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO context_handoff(ts, task_id, from_role, to_role, artifact_id, artifact_kind, consumed)
            VALUES($ts, $tid, $fr, $tr, $aid, $k, $c)
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString(IssueStore.DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$tid", taskId);
        cmd.Parameters.AddWithValue("$fr", fromRole);
        cmd.Parameters.AddWithValue("$tr", toRole);
        cmd.Parameters.AddWithValue("$aid", artifactId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$c", consumed ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Read the handoff lineage for a task. Most recent first.
    /// </summary>
    public async Task<IReadOnlyList<ContextHandoffEntry>> ListForTaskAsync(
        string taskId, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, task_id, from_role, to_role, artifact_id, artifact_kind, consumed
            FROM context_handoff
            WHERE task_id = $tid
            ORDER BY ts DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$tid", taskId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<ContextHandoffEntry>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new ContextHandoffEntry(
                Id: rd.GetInt64(0),
                Ts: DateTime.ParseExact(rd.GetString(1), IssueStore.DateFormat, CultureInfo.InvariantCulture),
                TaskId: rd.GetString(2),
                FromRole: rd.GetString(3),
                ToRole: rd.GetString(4),
                ArtifactId: rd.GetString(5),
                ArtifactKind: rd.GetString(6),
                Consumed: rd.GetInt32(7) != 0));
        }
        return list;
    }
}

public sealed record ContextHandoffEntry(
    long Id,
    DateTime Ts,
    string TaskId,
    string FromRole,
    string ToRole,
    string ArtifactId,
    string ArtifactKind,
    bool Consumed);
