using System.Text.Json;
using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>
/// P6 Stage 8: audit log for the SprintPropose service. One row per
/// /api/sprints/propose-next call. Schema v14 adds the
/// <c>sprint_proposal_audit</c> table; this class is the typed
/// access layer.
/// </summary>
public sealed class SprintProposalAuditStore : IAsyncDisposable
{
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly IDbConnectionFactory _db;

    public SprintProposalAuditStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
    }

    public SprintProposalAuditStore(IDbConnectionFactory db)
    {
        _db = db;
    }

    private static string BuildSqliteConnectionString(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    private string T(string name) => _db.Dialect.Table(name);

    public async Task<long> RecordAsync(
        string? theme,
        string? goal,
        IReadOnlyDictionary<string, object> weights,
        IReadOnlyList<SprintProposeCandidate> candidates,
        IReadOnlyList<string> selectedTaskIds,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                INSERT INTO {T("sprint_proposal_audit")}(
                    ts, theme, goal, weights_json, candidates_json,
                    selected_task_ids_json)
                OUTPUT INSERTED.id
                VALUES(@ts, @theme, @goal, @weights, @cands, @sel);
                """
            : """
                INSERT INTO sprint_proposal_audit(
                    ts, theme, goal, weights_json, candidates_json,
                    selected_task_ids_json)
                VALUES(@ts, @theme, @goal, @weights, @cands, @sel);
                SELECT last_insert_rowid();
                """;
        cmd.AddParam("@ts", DateTime.UtcNow.ToString(DateFormat));
        cmd.AddParam("@theme", (object?)theme ?? DBNull.Value);
        cmd.AddParam("@goal", (object?)goal ?? DBNull.Value);
        cmd.AddParam("@weights",
            JsonSerializer.Serialize(weights));
        cmd.AddParam("@cands",
            JsonSerializer.Serialize(candidates.Select(c => new
            {
                taskId = c.TaskId,
                title = c.Title,
                score = c.Score,
                breakdown = c.Breakdown,
            })));
        cmd.AddParam("@sel",
            JsonSerializer.Serialize(selectedTaskIds));
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return id;
    }

    public async Task MarkCommittedAsync(long id, string sprintId, string committedBy, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("sprint_proposal_audit")}
            SET committed_sprint_id = @sid, committed_by = @who
            WHERE id = @id
            """;
        cmd.AddParam("@sid", sprintId);
        cmd.AddParam("@who", committedBy);
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SprintProposalAuditRecord>> ListAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var d = _db.Dialect;
        cmd.CommandText = $"""
            SELECT {d.TopParam("@limit")}id, ts, theme, goal, weights_json, candidates_json,
                   selected_task_ids_json, committed_sprint_id, committed_by
            FROM {T("sprint_proposal_audit")}
            ORDER BY ts DESC, id DESC
            {d.LimitParam("@limit")}
            """;
        cmd.AddParam("@limit", limit);
        var list = new List<SprintProposalAuditRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(Read(rd));
        }
        return list;
    }

    public async Task<SprintProposalAuditRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, ts, theme, goal, weights_json, candidates_json,
                   selected_task_ids_json, committed_sprint_id, committed_by
            FROM {T("sprint_proposal_audit")}
            WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    private static SprintProposalAuditRecord Read(DbDataReader rd) => new(
        Id: rd.GetInt64(0),
        Timestamp: DateTime.ParseExact(rd.GetString(1), DateFormat,
            System.Globalization.CultureInfo.InvariantCulture),
        Theme: rd.IsDBNull(2) ? null : rd.GetString(2),
        Goal: rd.IsDBNull(3) ? null : rd.GetString(3),
        WeightsJson: rd.GetString(4),
        CandidatesJson: rd.GetString(5),
        SelectedTaskIdsJson: rd.GetString(6),
        CommittedSprintId: rd.IsDBNull(7) ? null : rd.GetString(7),
        CommittedBy: rd.IsDBNull(8) ? null : rd.GetString(8));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}

public sealed record SprintProposalAuditRecord(
    long Id,
    DateTime Timestamp,
    string? Theme,
    string? Goal,
    string WeightsJson,
    string CandidatesJson,
    string SelectedTaskIdsJson,
    string? CommittedSprintId,
    string? CommittedBy);

public sealed record SprintProposeCandidate(
    string TaskId,
    string Title,
    int Score,
    IReadOnlyList<string> Breakdown);