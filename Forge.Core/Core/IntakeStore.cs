using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Forge.Core;

public enum IntakeMessageRole
{
    User,
    Assistant,
    System,
}

/// <summary>
/// A single message in an intake conversation. <see cref="ProposedEpicId"/>
/// is set on assistant messages when the agent called the
/// <c>create_epic</c> AIFunction during this turn. The dashboard's
/// "Accept" button flips the linked issue's state and binds it to the
/// active sprint.
/// </summary>
public sealed record IntakeMessageRecord(
    long Id,
    string SessionId,
    IntakeMessageRole Role,
    string Content,
    DateTime Timestamp,
    string? ProposedEpicId = null,
    string? ProposedEpicTitle = null);

public sealed record IntakeSessionRecord(
    string Id,
    string ProjectId,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<IntakeMessageRecord> Messages);

public sealed record NewIntakeMessage(
    IntakeMessageRole Role,
    string Content,
    string? ProposedEpicId = null,
    string? ProposedEpicTitle = null);

public interface IIntakeStore
{
    Task<IntakeSessionRecord> CreateAsync(string projectId, string? title, CancellationToken ct = default);
    Task<IntakeSessionRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<IntakeSessionRecord>> ListAsync(CancellationToken ct = default);
    Task<IntakeMessageRecord> AppendMessageAsync(string sessionId, NewIntakeMessage message, CancellationToken ct = default);
    /// <summary>Replace the entire message list for a session (used to roll back a failed agent run).</summary>
    Task SetMessagesAsync(string sessionId, IReadOnlyList<NewIntakeMessage> messages, CancellationToken ct = default);
}

public sealed class IntakeStore : IIntakeStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public IntakeStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public async Task<IntakeSessionRecord> CreateAsync(string projectId, string? title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        var now = DateTime.UtcNow;
        var id = $"intake-{Guid.NewGuid():N}";
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "New intake" : title;
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {T("intake_session")} (id, project_id, title, created_at, updated_at)
            VALUES (@id, @proj, @title, @now, @now)
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@proj", projectId);
        cmd.AddParam("@title", resolvedTitle);
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
        await cmd.ExecuteNonQueryAsync(ct);
        return new IntakeSessionRecord(id, projectId, resolvedTitle, now, now, Array.Empty<IntakeMessageRecord>());
    }

    public async Task<IntakeSessionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);

        IntakeSessionRecord? session = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, project_id, title, created_at, updated_at FROM {T("intake_session")} WHERE id = @id";
            cmd.AddParam("@id", id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            session = new IntakeSessionRecord(
                Id: rd.GetString(0),
                ProjectId: rd.GetString(1),
                Title: rd.GetString(2),
                CreatedAt: IssueStore.ParseTime(rd.GetString(3)),
                UpdatedAt: IssueStore.ParseTime(rd.GetString(4)),
                Messages: Array.Empty<IntakeMessageRecord>());
        }

        var messages = await LoadMessagesAsync(conn, id, ct);
        return session with { Messages = messages };
    }

    public async Task<IReadOnlyList<IntakeSessionRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);

        var sessions = new List<IntakeSessionRecord>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, project_id, title, created_at, updated_at FROM {T("intake_session")} ORDER BY updated_at DESC";
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                sessions.Add(new IntakeSessionRecord(
                    Id: rd.GetString(0),
                    ProjectId: rd.GetString(1),
                    Title: rd.GetString(2),
                    CreatedAt: IssueStore.ParseTime(rd.GetString(3)),
                    UpdatedAt: IssueStore.ParseTime(rd.GetString(4)),
                    Messages: Array.Empty<IntakeMessageRecord>()));
            }
        }

        // Lazy-load messages per session. For very long sessions this could
        // be a single query, but intake sessions are operator-driven and
        // short-lived; a per-session read keeps memory bounded.
        var result = new List<IntakeSessionRecord>(sessions.Count);
        foreach (var s in sessions)
        {
            var messages = await LoadMessagesAsync(conn, s.Id, ct);
            result.Add(s with { Messages = messages });
        }
        return result;
    }

    public async Task<IntakeMessageRecord> AppendMessageAsync(string sessionId, NewIntakeMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(message.Content))
            throw new ArgumentException("content is required", nameof(message));

        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);

        // Verify the session exists; cheap and gives a clear error.
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT 1 FROM {T("intake_session")} WHERE id = @id";
            check.AddParam("@id", sessionId);
            var hit = await check.ExecuteScalarAsync(ct);
            if (hit is null) throw new InvalidOperationException($"Intake session {sessionId} not found");
        }

        long id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
                ? $"""
                    INSERT INTO {T("intake_message")}
                    (session_id, role, content, ts, proposed_epic_id, proposed_epic_title)
                    OUTPUT INSERTED.id
                    VALUES (@sid, @role, @content, @ts, @epicId, @epicTitle);
                    """
                : """
                    INSERT INTO intake_message
                    (session_id, role, content, ts, proposed_epic_id, proposed_epic_title)
                    VALUES (@sid, @role, @content, @ts, @epicId, @epicTitle);
                    SELECT last_insert_rowid();
                    """;
            cmd.AddParam("@sid", sessionId);
            cmd.AddParam("@role", message.Role.ToString());
            cmd.AddParam("@content", message.Content);
            cmd.AddParam("@ts", IssueStore.DateFormatTime(now));
            cmd.AddParam("@epicId", (object?)message.ProposedEpicId ?? DBNull.Value);
            cmd.AddParam("@epicTitle", (object?)message.ProposedEpicTitle ?? DBNull.Value);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        await using (var upd = conn.CreateCommand())
        {
            upd.CommandText = $"""UPDATE {T("intake_session")} SET updated_at = @now WHERE id = @id""";
            upd.AddParam("@now", IssueStore.DateFormatTime(now));
            upd.AddParam("@id", sessionId);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return new IntakeMessageRecord(id, sessionId, message.Role, message.Content, now,
            message.ProposedEpicId, message.ProposedEpicTitle);
    }

    public async Task SetMessagesAsync(string sessionId, IReadOnlyList<NewIntakeMessage> messages, CancellationToken ct = default)
    {
        // Used by the agent runner to roll back a turn if the LLM call
        // throws after we've already appended the user message.
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {T("intake_message")} WHERE session_id = @sid";
            del.AddParam("@sid", sessionId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var m in messages)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = $"""
                INSERT INTO {T("intake_message")}
                (session_id, role, content, ts, proposed_epic_id, proposed_epic_title)
                VALUES (@sid, @role, @content, @now, @epicId, @epicTitle)
                """;
            ins.AddParam("@sid", sessionId);
            ins.AddParam("@role", m.Role.ToString());
            ins.AddParam("@content", m.Content);
            ins.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            ins.AddParam("@epicId", (object?)m.ProposedEpicId ?? DBNull.Value);
            ins.AddParam("@epicTitle", (object?)m.ProposedEpicTitle ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task<IReadOnlyList<IntakeMessageRecord>> LoadMessagesAsync(
        DbConnection conn, string sessionId, CancellationToken ct)
    {
        var list = new List<IntakeMessageRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, session_id, role, content, ts, proposed_epic_id, proposed_epic_title
            FROM {T("intake_message")} WHERE session_id = @sid ORDER BY id
            """;
        cmd.AddParam("@sid", sessionId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new IntakeMessageRecord(
                Id: rd.GetInt64(0),
                SessionId: rd.GetString(1),
                Role: Enum.Parse<IntakeMessageRole>(rd.GetString(2)),
                Content: rd.GetString(3),
                Timestamp: IssueStore.ParseTime(rd.GetString(4)),
                ProposedEpicId: rd.IsDBNull(5) ? null : rd.GetString(5),
                ProposedEpicTitle: rd.IsDBNull(6) ? null : rd.GetString(6)));
        }
        return list;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}

/// <summary>
/// No-op intake store used when intake is not configured (e.g. tests
/// that don't exercise the intake path, or the smoke command-line
/// mode that runs without the dashboard). All operations throw
/// <see cref="NotSupportedException"/> except <see cref="ListAsync"/>
/// (which returns an empty list).
/// </summary>
public sealed class NullIntakeStore : IIntakeStore
{
    public Task<IntakeSessionRecord> CreateAsync(string projectId, string? title, CancellationToken ct = default)
        => throw new NotSupportedException("Intake is not configured on this dashboard instance.");
    public Task<IntakeSessionRecord?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult<IntakeSessionRecord?>(null);
    public Task<IReadOnlyList<IntakeSessionRecord>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IntakeSessionRecord>>(Array.Empty<IntakeSessionRecord>());
    public Task<IntakeMessageRecord> AppendMessageAsync(string sessionId, NewIntakeMessage message, CancellationToken ct = default)
        => throw new NotSupportedException("Intake is not configured on this dashboard instance.");
    public Task SetMessagesAsync(string sessionId, IReadOnlyList<NewIntakeMessage> messages, CancellationToken ct = default)
        => throw new NotSupportedException("Intake is not configured on this dashboard instance.");
}
