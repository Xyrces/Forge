using System.Globalization;
using System.Text.Json;
using System.Data.Common;
using Forge.Core.Db;
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
    private readonly IDbConnectionFactory _db;
    private readonly string _dbPath;

    public DesignerRunStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
        _dbPath = dbPath;
    }

    public DesignerRunStore(IDbConnectionFactory db)
    {
        _db = db;
        _dbPath = "";
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

    public string DbPath => _dbPath;

    public async Task<DesignerRun> StartAsync(
        string specId, DesignerTriggerKind trigger, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                INSERT INTO {T("designer_run")}(ts, spec_id, trigger_kind, status)
                OUTPUT INSERTED.id
                VALUES(@ts, @spec, @trigger, 'started');
                """
            : """
                INSERT INTO designer_run(ts, spec_id, trigger_kind, status)
                VALUES(@ts, @spec, @trigger, 'started')
                RETURNING id
                """;
        cmd.AddParam("@ts", now.ToString(IssueStore.DateFormat));
        cmd.AddParam("@spec", specId);
        cmd.AddParam("@trigger", trigger.ToString().ToLowerInvariant());
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
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("designer_run")}
            SET status = @status,
                new_spec_status = @new_status,
                design_artifact_ids = @artifact_ids,
                hygiene_report = @hygiene,
                error = @error,
                duration_ms = @ms
            WHERE id = @id
            """;
        cmd.AddParam("@status", StatusToDb(status));
        cmd.AddParam("@new_status", (object?)newSpecStatus?.ToString() ?? DBNull.Value);
        cmd.AddParam("@artifact_ids",
            (object?)(designArtifactIds is null ? null : JsonSerializer.Serialize(designArtifactIds)) ?? DBNull.Value);
        cmd.AddParam("@hygiene", (object?)hygieneReportJson ?? DBNull.Value);
        cmd.AddParam("@error", (object?)error ?? DBNull.Value);
        cmd.AddParam("@ms", (long)duration.TotalMilliseconds);
        cmd.AddParam("@id", runId);
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
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var d = _db.Dialect;
        if (specId is null)
        {
            cmd.CommandText = $"""
                SELECT {d.TopParam("@limit")}id, ts, spec_id, trigger_kind, status, new_spec_status,
                       design_artifact_ids, hygiene_report, error, duration_ms
                FROM {T("designer_run")}
                ORDER BY ts DESC
                {d.LimitParam("@limit")}
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT {d.TopParam("@limit")}id, ts, spec_id, trigger_kind, status, new_spec_status,
                       design_artifact_ids, hygiene_report, error, duration_ms
                FROM {T("designer_run")}
                WHERE spec_id = @spec
                ORDER BY ts DESC
                {d.LimitParam("@limit")}
                """;
            cmd.AddParam("@spec", specId);
        }
        cmd.AddParam("@limit", limit);
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