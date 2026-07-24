using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// Agent run registry: one row per agent run, written at run start
/// (near-real-time "who is doing what") and finished with the
/// outcome + full conversation transcript ("see their work").
///
/// <para>
/// Retention protects the storage budget: on every finish, runs
/// older than 30 days are deleted and each task keeps only its
/// newest 50 runs. Transcripts are full-fidelity within those
/// bounds — we prune history, not content.
/// </para>
/// </summary>
public sealed class AgentRunStore : IDisposable
{
    private const string DateFormat = IssueStore.DateFormat;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const int MaxRunsPerTask = 50;

    private readonly string _connectionString;

    public AgentRunStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public sealed record AgentRunRecord(
        string Id,
        string? TaskId,
        string Role,
        string? Model,
        string Status,          // running | succeeded | failed
        DateTime StartedAt,
        DateTime? FinishedAt,
        long? DurationMs,
        int? MessageCount,
        int? ToolCallCount,
        int? TextChars,
        string? Error,
        string? TranscriptJson);

    public async Task StartAsync(string id, string? taskId, string role, string? model, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO agent_run
                (id, task_id, role, model, status, started_at)
            VALUES ($id, $task, $role, $model, 'running', $started)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString(DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishAsync(
        string id, string status, long durationMs,
        int messageCount, int toolCallCount, int textChars,
        string? error, string? transcriptJson, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_run SET
                status = $status, finished_at = $finished, duration_ms = $dur,
                message_count = $msgs, tool_call_count = $tools, text_chars = $chars,
                error = $err, transcript_json = $transcript
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$finished", DateTime.UtcNow.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$dur", durationMs);
        cmd.Parameters.AddWithValue("$msgs", messageCount);
        cmd.Parameters.AddWithValue("$tools", toolCallCount);
        cmd.Parameters.AddWithValue("$chars", textChars);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$transcript", (object?)transcriptJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        await PruneAsync(conn, ct);
    }

    private static async Task PruneAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using (var age = conn.CreateCommand())
        {
            age.CommandText = "DELETE FROM agent_run WHERE started_at < $cutoff";
            age.Parameters.AddWithValue("$cutoff", (DateTime.UtcNow - Retention).ToString(DateFormat));
            await age.ExecuteNonQueryAsync(ct);
        }
        await using (var perTask = conn.CreateCommand())
        {
            perTask.CommandText = """
                DELETE FROM agent_run WHERE task_id IS NOT NULL AND id NOT IN (
                    SELECT id FROM agent_run r2
                    WHERE r2.task_id = agent_run.task_id
                    ORDER BY started_at DESC LIMIT 50)
                """;
            await perTask.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<AgentRunRecord>> ListActiveAsync(CancellationToken ct = default)
        => await QueryAsync("WHERE status = 'running' ORDER BY started_at DESC", ct);

    public async Task<IReadOnlyList<AgentRunRecord>> ListRecentAsync(int limit = 50, string? taskId = null, CancellationToken ct = default)
        => await QueryAsync(
            taskId is null
                ? $"WHERE status != 'running' ORDER BY started_at DESC LIMIT {Math.Clamp(limit, 1, 500)}"
                : $"WHERE task_id = '{taskId.Replace("'", "''")}' ORDER BY started_at DESC LIMIT {Math.Clamp(limit, 1, 500)}",
            ct);

    public async Task<AgentRunRecord?> GetAsync(string id, CancellationToken ct = default)
        => (await QueryAsync($"WHERE id = '{id.Replace("'", "''")}' LIMIT 1", ct)).FirstOrDefault();

    private async Task<IReadOnlyList<AgentRunRecord>> QueryAsync(string whereSql, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, task_id, role, model, status, started_at, finished_at,
                   duration_ms, message_count, tool_call_count, text_chars, error, transcript_json
            FROM agent_run {whereSql}
            """;
        var list = new List<AgentRunRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new AgentRunRecord(
                Id: rd.GetString(0),
                TaskId: rd.IsDBNull(1) ? null : rd.GetString(1),
                Role: rd.GetString(2),
                Model: rd.IsDBNull(3) ? null : rd.GetString(3),
                Status: rd.GetString(4),
                StartedAt: DateTime.ParseExact(rd.GetString(5), DateFormat, System.Globalization.CultureInfo.InvariantCulture),
                FinishedAt: rd.IsDBNull(6) ? null : DateTime.ParseExact(rd.GetString(6), DateFormat, System.Globalization.CultureInfo.InvariantCulture),
                DurationMs: rd.IsDBNull(7) ? null : rd.GetInt64(7),
                MessageCount: rd.IsDBNull(8) ? null : rd.GetInt32(8),
                ToolCallCount: rd.IsDBNull(9) ? null : rd.GetInt32(9),
                TextChars: rd.IsDBNull(10) ? null : rd.GetInt32(10),
                Error: rd.IsDBNull(11) ? null : rd.GetString(11),
                TranscriptJson: rd.IsDBNull(12) ? null : rd.GetString(12)));
        }
        return list;
    }

    public void Dispose() { }
}
