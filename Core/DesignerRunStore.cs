using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// One row per Designer agent run. Mirrors <see cref="IssueGroomerRunStore"/>
/// (which is the run log for the Groomer). The run log + the per-run
/// hygiene verdict land in one row so the dashboard's Design tab
/// can show the operator "what the Designer did, when, and what
/// hygiene issues it found" in a single read.
///
/// <para>
/// Three status values: <c>started</c>, <c>succeeded</c>,
/// <c>hygiene_failed</c> (deterministic check rejected the spec
/// before the LLM ran), <c>llm_failed</c> (LLM call threw or
/// returned a malformed response). <c>succeeded</c> covers both
/// "designed a visual" and "approved non-visual" — the
/// <c>new_spec_status</c> column disambiguates.
/// </para>
/// </summary>
public sealed class DesignerRunStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public DesignerRunStore(string dbPath)
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

    public async Task<DesignerRun> StartAsync(
        string specId, DesignerTriggerKind trigger, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO designer_run(ts, spec_id, trigger_kind, status)
            VALUES($ts, $spec, $trigger, 'started')
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$ts", now.ToString(IssueStore.DateFormat));
        cmd.Parameters.AddWithValue("$spec", specId);
        cmd.Parameters.AddWithValue("$trigger", trigger.ToString().ToLowerInvariant());
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return new DesignerRun(
            Id: id, Ts: now, SpecId: specId, Trigger: trigger,
            Status: DesignerRunStatus.Started, NewSpecStatus: null,
            DesignArtifactIds: null, HygieneReportJson: null,
            Error: null, DurationMs: 0);
    }

    public async Task<DesignerRun> FinishAsync(
        long runId,
        DesignerRunStatus status,
        SpecStatus? newSpecStatus,
        IReadOnlyList<string>? designArtifactIds,
        string? hygieneReportJson,
        string? error,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE designer_run
            SET status = $status,
                new_spec_status = $new_status,
                design_artifact_ids = $artifact_ids,
                hygiene_report = $hygiene,
                error = $error,
                duration_ms = $ms
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$status", StatusToDb(status));
        cmd.Parameters.AddWithValue("$new_status", (object?)newSpecStatus?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artifact_ids",
            (object?)(designArtifactIds is null ? null : JsonSerializer.Serialize(designArtifactIds)) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hygiene", (object?)hygieneReportJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", (long)duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync(ct);
        return new DesignerRun(
            Id: runId, Ts: DateTime.MinValue, SpecId: "", Trigger: DesignerTriggerKind.Manual,
            Status: status, NewSpecStatus: newSpecStatus, DesignArtifactIds: designArtifactIds,
            HygieneReportJson: hygieneReportJson, Error: error,
            DurationMs: (long)duration.TotalMilliseconds);
    }

    public async Task<IReadOnlyList<DesignerRun>> ListAsync(
        string? specId = null, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (specId is null)
        {
            cmd.CommandText = """
                SELECT id, ts, spec_id, trigger_kind, status, new_spec_status,
                       design_artifact_ids, hygiene_report, error, duration_ms
                FROM designer_run
                ORDER BY ts DESC
                LIMIT $limit
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT id, ts, spec_id, trigger_kind, status, new_spec_status,
                       design_artifact_ids, hygiene_report, error, duration_ms
                FROM designer_run
                WHERE spec_id = $spec
                ORDER BY ts DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$spec", specId);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<DesignerRun>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new DesignerRun(
                Id: rd.GetInt64(0),
                Ts: DateTime.ParseExact(rd.GetString(1), IssueStore.DateFormat, CultureInfo.InvariantCulture),
                SpecId: rd.GetString(2),
                Trigger: ParseTrigger(rd.GetString(3)),
                Status: ParseStatus(rd.GetString(4)),
                NewSpecStatus: rd.IsDBNull(5) ? null : Enum.Parse<SpecStatus>(rd.GetString(5)),
                DesignArtifactIds: rd.IsDBNull(6) ? null : JsonSerializer.Deserialize<List<string>>(rd.GetString(6)),
                HygieneReportJson: rd.IsDBNull(7) ? null : rd.GetString(7),
                Error: rd.IsDBNull(8) ? null : rd.GetString(8),
                DurationMs: rd.GetInt32(9)));
        }
        return list;
    }

    private static string StatusToDb(DesignerRunStatus s) => s switch
    {
        DesignerRunStatus.Started => "started",
        DesignerRunStatus.Succeeded => "succeeded",
        DesignerRunStatus.HygieneFailed => "hygiene_failed",
        DesignerRunStatus.LlmFailed => "llm_failed",
        _ => "llm_failed",
    };

    private static DesignerRunStatus ParseStatus(string s) => s switch
    {
        "started" => DesignerRunStatus.Started,
        "succeeded" => DesignerRunStatus.Succeeded,
        "hygiene_failed" => DesignerRunStatus.HygieneFailed,
        "llm_failed" => DesignerRunStatus.LlmFailed,
        _ => DesignerRunStatus.LlmFailed,
    };

    private static DesignerTriggerKind ParseTrigger(string s) => s switch
    {
        "manual" => DesignerTriggerKind.Manual,
        "scheduled" => DesignerTriggerKind.Scheduled,
        _ => DesignerTriggerKind.Manual,
    };
}

public enum DesignerTriggerKind { Manual, Scheduled }

public enum DesignerRunStatus { Started, Succeeded, HygieneFailed, LlmFailed }

public sealed record DesignerRun(
    long Id,
    DateTime Ts,
    string SpecId,
    DesignerTriggerKind Trigger,
    DesignerRunStatus Status,
    SpecStatus? NewSpecStatus,
    IReadOnlyList<string>? DesignArtifactIds,
    string? HygieneReportJson,
    string? Error,
    long DurationMs);