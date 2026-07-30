using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// One row per Artist agent run. Mirrors <see cref="DesignerRunStore"/>
/// (the run log for the Designer). The run log + the per-run
/// Meshy task list + the produced <c>art_output</c> ids land in
/// one row so the dashboard's Art tab can show the operator
/// "what the Artist did, when, and which Meshy jobs it kicked
/// off" in a single read.
///
/// <para>
/// Four status values: <c>started</c>, <c>succeeded</c>,
/// <c>meshy_failed</c> (a Meshy job returned FAILED or timed
/// out), <c>llm_failed</c> (LLM call threw or returned a
/// malformed response). <c>succeeded</c> covers "produced all
/// art outputs" — the <c>new_spec_status</c> column
/// disambiguates <c>asset_ready</c> from
/// <c>needs_revision</c>.
/// </para>
/// </summary>
public sealed class ArtistRunStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public ArtistRunStore(string dbPath)
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

    public async Task<ArtistRun> StartAsync(
        string specId, ArtistTriggerKind trigger, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artist_run(ts, spec_id, trigger_kind, status)
            VALUES($ts, $spec, $trigger, 'started')
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$ts", now.ToString(IssueStore.DateFormat));
        cmd.Parameters.AddWithValue("$spec", specId);
        cmd.Parameters.AddWithValue("$trigger", trigger.ToString().ToLowerInvariant());
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return new ArtistRun(
            Id: id, Ts: now, SpecId: specId, Trigger: trigger,
            Status: ArtistRunStatus.Started, NewSpecStatus: null,
            ArtOutputIds: null, MeshyTasks: null,
            Error: null, DurationMs: 0);
    }

    public async Task<ArtistRun> FinishAsync(
        long runId,
        ArtistRunStatus status,
        SpecStatus? newSpecStatus,
        IReadOnlyList<string>? artOutputIds,
        IReadOnlyList<MeshyTaskRecord>? meshyTasks,
        string? error,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE artist_run
            SET status = $status,
                new_spec_status = $new_status,
                art_output_ids = $artifact_ids,
                meshy_tasks = $tasks,
                error = $error,
                duration_ms = $ms
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$status", StatusToDb(status));
        cmd.Parameters.AddWithValue("$new_status", (object?)newSpecStatus?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artifact_ids",
            (object?)(artOutputIds is null ? null : JsonSerializer.Serialize(artOutputIds)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tasks",
            (object?)(meshyTasks is null ? null : JsonSerializer.Serialize(meshyTasks)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", (long)duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync(ct);
        return new ArtistRun(
            Id: runId, Ts: DateTime.MinValue, SpecId: "", Trigger: ArtistTriggerKind.Manual,
            Status: status, NewSpecStatus: newSpecStatus, ArtOutputIds: artOutputIds,
            MeshyTasks: meshyTasks, Error: error,
            DurationMs: (long)duration.TotalMilliseconds);
    }

    public async Task<IReadOnlyList<ArtistRun>> ListAsync(
        string? specId = null, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (specId is null)
        {
            cmd.CommandText = """
                SELECT id, ts, spec_id, trigger_kind, status, new_spec_status,
                       art_output_ids, meshy_tasks, error, duration_ms
                FROM artist_run
                ORDER BY ts DESC
                LIMIT $limit
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT id, ts, spec_id, trigger_kind, status, new_spec_status,
                       art_output_ids, meshy_tasks, error, duration_ms
                FROM artist_run
                WHERE spec_id = $spec
                ORDER BY ts DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$spec", specId);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<ArtistRun>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new ArtistRun(
                Id: rd.GetInt64(0),
                Ts: DateTime.ParseExact(rd.GetString(1), IssueStore.DateFormat, CultureInfo.InvariantCulture),
                SpecId: rd.GetString(2),
                Trigger: ParseTrigger(rd.GetString(3)),
                Status: ParseStatus(rd.GetString(4)),
                NewSpecStatus: rd.IsDBNull(5) ? null : Enum.Parse<SpecStatus>(rd.GetString(5)),
                ArtOutputIds: rd.IsDBNull(6) ? null : JsonSerializer.Deserialize<List<string>>(rd.GetString(6)),
                MeshyTasks: rd.IsDBNull(7) ? null : JsonSerializer.Deserialize<List<MeshyTaskRecord>>(rd.GetString(7)),
                Error: rd.IsDBNull(8) ? null : rd.GetString(8),
                DurationMs: rd.GetInt32(9)));
        }
        return list;
    }

    private static string StatusToDb(ArtistRunStatus s) => s switch
    {
        ArtistRunStatus.Started => "started",
        ArtistRunStatus.Succeeded => "succeeded",
        ArtistRunStatus.MeshyFailed => "meshy_failed",
        ArtistRunStatus.LlmFailed => "llm_failed",
        _ => "llm_failed",
    };

    private static ArtistRunStatus ParseStatus(string s) => s switch
    {
        "started" => ArtistRunStatus.Started,
        "succeeded" => ArtistRunStatus.Succeeded,
        "meshy_failed" => ArtistRunStatus.MeshyFailed,
        "llm_failed" => ArtistRunStatus.LlmFailed,
        _ => ArtistRunStatus.LlmFailed,
    };

    private static ArtistTriggerKind ParseTrigger(string s) => s switch
    {
        "manual" => ArtistTriggerKind.Manual,
        "scheduled" => ArtistTriggerKind.Scheduled,
        _ => ArtistTriggerKind.Manual,
    };
}

public enum ArtistTriggerKind { Manual, Scheduled }

public enum ArtistRunStatus { Started, Succeeded, MeshyFailed, LlmFailed }

public sealed record ArtistRun(
    long Id,
    DateTime Ts,
    string SpecId,
    ArtistTriggerKind Trigger,
    ArtistRunStatus Status,
    SpecStatus? NewSpecStatus,
    IReadOnlyList<string>? ArtOutputIds,
    IReadOnlyList<MeshyTaskRecord>? MeshyTasks,
    string? Error,
    long DurationMs);

public sealed record MeshyTaskRecord(
    string Id,
    string Mode,                // "text-to-3d" | "image-to-3d" | "rigging"
    string Status,              // "PENDING" | "IN_PROGRESS" | "SUCCEEDED" | "FAILED" | "CANCELED"
    string? ArtOutputId,        // null while pending; populated when SUCCEEDED
    string? GlbUrl);            // raw Meshy signed URL (signed URLs expire in ~1h)
