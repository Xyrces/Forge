using System.Data.Common;
using Forge.Core.Db;
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

    private readonly IDbConnectionFactory _db;

    public AgentRunStore(string dbPath)
        : this(ForgeDb.Sqlite(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString()))
    {
    }

    public AgentRunStore(IDbConnectionFactory db)
    {
        _db = db;
    }

    private string T(string name) => _db.Dialect.Table(name);

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
        string? TranscriptJson,
        DateTime? LastActivityAt,
        // v25: live phase label (plan gate / implementing /
        // verifying n/3 / reviewing) + the warm-session marker
        // (the run resumed a persisted MAF session).
        string? Phase,
        bool? ResumedSession);

    public async Task StartAsync(string id, string? taskId, string role, string? model, CancellationToken ct = default,
        bool resumedSession = false)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                MERGE {T("agent_run")} WITH (HOLDLOCK) AS t
                USING (SELECT @id AS id) AS s ON t.id = s.id
                WHEN MATCHED THEN UPDATE SET task_id = @task, role = @role, model = @model,
                    status = 'running', started_at = @started, finished_at = NULL, duration_ms = NULL,
                    message_count = NULL, tool_call_count = NULL, text_chars = NULL,
                    error = NULL, transcript_json = NULL, last_activity_at = NULL,
                    phase = NULL, resumed_session = @resumed
                WHEN NOT MATCHED THEN INSERT (id, task_id, role, model, status, started_at, resumed_session)
                    VALUES (@id, @task, @role, @model, 'running', @started, @resumed);
                """
            : """
                INSERT OR REPLACE INTO agent_run
                    (id, task_id, role, model, status, started_at, resumed_session)
                VALUES (@id, @task, @role, @model, 'running', @started, @resumed)
                """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@task", (object?)taskId ?? DBNull.Value);
        cmd.AddParam("@role", role);
        cmd.AddParam("@model", (object?)model ?? DBNull.Value);
        cmd.AddParam("@started", DateTime.UtcNow.ToString(DateFormat));
        cmd.AddParam("@resumed", resumedSession ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Mid-run heartbeat: updates turn/tool-call counts and stamps
    /// last_activity_at so the dashboard can distinguish "agent is
    /// actively working" from "run has been waiting on the provider
    /// with no output for N minutes". When <paramref name="transcriptJson"/>
    /// is provided the accumulated live transcript is persisted too —
    /// the run-detail page streams the conversation AS IT HAPPENS.
    /// Cheap single-row UPDATE; the activity tracker calls it after
    /// every model round-trip. <paramref name="phase"/> is the run's
    /// live phase label (plan gate / implementing / verifying n/3 /
    /// reviewing); null keeps the previously written value.
    /// </summary>
    public async Task UpdateProgressAsync(string id, int messageCount, int toolCallCount, int textChars, string? transcriptJson = null, CancellationToken ct = default, string? phase = null)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("agent_run")} SET
                message_count = @msgs, tool_call_count = @tools,
                text_chars = @chars, last_activity_at = @activity,
                transcript_json = COALESCE(@transcript, transcript_json),
                phase = COALESCE(@phase, phase)
            WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@msgs", messageCount);
        cmd.AddParam("@tools", toolCallCount);
        cmd.AddParam("@chars", textChars);
        cmd.AddParam("@activity", DateTime.UtcNow.ToString(DateFormat));
        cmd.AddParam("@transcript", (object?)transcriptJson ?? DBNull.Value);
        cmd.AddParam("@phase", (object?)phase ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishAsync(
        string id, string status, long durationMs,
        int messageCount, int toolCallCount, int textChars,
        string? error, string? transcriptJson, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("agent_run")} SET
                status = @status, finished_at = @finished, duration_ms = @dur,
                message_count = @msgs, tool_call_count = @tools, text_chars = @chars,
                error = @err, transcript_json = @transcript, last_activity_at = @finished
            WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@status", status);
        cmd.AddParam("@finished", DateTime.UtcNow.ToString(DateFormat));
        cmd.AddParam("@dur", durationMs);
        cmd.AddParam("@msgs", messageCount);
        cmd.AddParam("@tools", toolCallCount);
        cmd.AddParam("@chars", textChars);
        cmd.AddParam("@err", (object?)error ?? DBNull.Value);
        cmd.AddParam("@transcript", (object?)transcriptJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        await PruneAsync(conn, ct);
    }

    private async Task PruneAsync(DbConnection conn, CancellationToken ct)
    {
        await using (var age = conn.CreateCommand())
        {
            age.CommandText = $"DELETE FROM {T("agent_run")} WHERE started_at < @cutoff";
            age.AddParam("@cutoff", (DateTime.UtcNow - Retention).ToString(DateFormat));
            await age.ExecuteNonQueryAsync(ct);
        }
        await using (var perTask = conn.CreateCommand())
        {
            perTask.CommandText = _db.Provider == ForgeDbProvider.SqlServer
                ? $"""
                    DELETE a FROM {T("agent_run")} a
                    WHERE a.task_id IS NOT NULL AND a.id NOT IN (
                        SELECT TOP ({MaxRunsPerTask}) id FROM {T("agent_run")} r2
                        WHERE r2.task_id = a.task_id
                        ORDER BY started_at DESC)
                    """
                : $"""
                    DELETE FROM agent_run WHERE task_id IS NOT NULL AND id NOT IN (
                        SELECT id FROM agent_run r2
                        WHERE r2.task_id = agent_run.task_id
                        ORDER BY started_at DESC LIMIT {MaxRunsPerTask})
                    """;
            await perTask.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<AgentRunRecord>> ListActiveAsync(CancellationToken ct = default)
        => await QueryAsync("WHERE status = 'running' ORDER BY started_at DESC", null, ct);

    public async Task<IReadOnlyList<AgentRunRecord>> ListRecentAsync(int limit = 50, string? taskId = null, string? role = null, CancellationToken ct = default)
    {
        var where = "WHERE status != 'running'";
        if (taskId is not null) where += $" AND task_id = '{taskId.Replace("'", "''")}'";
        if (role is not null) where += $" AND role = '{role.Replace("'", "''")}'";
        return await QueryAsync($"{where} ORDER BY started_at DESC", Math.Clamp(limit, 1, 500), ct);
    }

    public async Task<AgentRunRecord?> GetAsync(string id, CancellationToken ct = default)
        => (await QueryAsync($"WHERE id = '{id.Replace("'", "''")}'", 1, ct)).FirstOrDefault();

    private async Task<IReadOnlyList<AgentRunRecord>> QueryAsync(string whereSql, int? limit, CancellationToken ct)
    {
        var d = _db.Dialect;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {(limit is { } n ? d.Top(n) : "")}id, task_id, role, model, status, started_at, finished_at,
                   duration_ms, message_count, tool_call_count, text_chars, error, transcript_json,
                   last_activity_at, phase, resumed_session
            FROM {T("agent_run")} {whereSql}{(limit is { } m ? d.Limit(m) : "")}
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
                TranscriptJson: rd.IsDBNull(12) ? null : rd.GetString(12),
                LastActivityAt: rd.IsDBNull(13) ? null : DateTime.ParseExact(rd.GetString(13), DateFormat, System.Globalization.CultureInfo.InvariantCulture),
                Phase: rd.IsDBNull(14) ? null : rd.GetString(14),
                ResumedSession: rd.IsDBNull(15) ? null : rd.GetInt32(15) != 0));
        }
        return list;
    }

    public void Dispose() { }
}
