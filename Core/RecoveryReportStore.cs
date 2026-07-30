using System.Globalization;
using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// CRUD for <see cref="RecoveryReportRecord"/> rows. The
/// StartupRecovery pass writes one row per startup pass at the
/// end of the run; the dashboard's Recovery tab reads them
/// via <see cref="ListAsync"/>.
/// </summary>
public sealed class RecoveryReportStore
{
    private readonly IDbConnectionFactory _db;
    private readonly string _dbPath;

    public RecoveryReportStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
        _dbPath = dbPath;
    }

    public RecoveryReportStore(IDbConnectionFactory db)
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

    public async Task<RecoveryReportRecord> StartAsync(string? specId, CancellationToken ct = default)
    {
        // The Start row is updated by FinishAsync; the Id round-trips
        // through the connection so the recoverer's caller can hold
        // the report id while doing the sweep.
        var now = DateTime.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                INSERT INTO {T("recovery_report")}(ts, spec_id, issues_scanned, issues_replayed, issues_failed, actions_json, duration_ms)
                OUTPUT INSERTED.id
                VALUES(@ts, @spec, 0, 0, 0, '[]', 0);
                """
            : """
                INSERT INTO recovery_report(ts, spec_id, issues_scanned, issues_replayed, issues_failed, actions_json, duration_ms)
                VALUES(@ts, @spec, 0, 0, 0, '[]', 0)
                RETURNING id
                """;
        cmd.AddParam("@ts", now.ToString(IssueStore.DateFormat));
        cmd.AddParam("@spec", (object?)specId ?? DBNull.Value);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return new RecoveryReportRecord(id, now, specId, 0, 0, 0, "[]", 0);
    }

    public async Task<RecoveryReportRecord> FinishAsync(
        long reportId,
        int issuesScanned,
        int issuesReplayed,
        int issuesFailed,
        IReadOnlyList<RecoveryActionRecord> actions,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        var actionsJson = System.Text.Json.JsonSerializer.Serialize(actions);
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("recovery_report")}
            SET issues_scanned = @scanned,
                issues_replayed = @replayed,
                issues_failed = @failed,
                actions_json = @actions,
                duration_ms = @ms
            WHERE id = @id
            """;
        cmd.AddParam("@id", reportId);
        cmd.AddParam("@scanned", issuesScanned);
        cmd.AddParam("@replayed", issuesReplayed);
        cmd.AddParam("@failed", issuesFailed);
        cmd.AddParam("@actions", actionsJson);
        cmd.AddParam("@ms", (long)duration.TotalMilliseconds);
        await cmd.ExecuteNonQueryAsync(ct);
        return new RecoveryReportRecord(
            reportId, DateTime.MinValue, null,
            issuesScanned, issuesReplayed, issuesFailed,
            actionsJson, (long)duration.TotalMilliseconds);
    }

    public async Task<IReadOnlyList<RecoveryReportRecord>> ListAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var d = _db.Dialect;
        cmd.CommandText = $"""
            SELECT {d.TopParam("@limit")}id, ts, spec_id, issues_scanned, issues_replayed, issues_failed, actions_json, duration_ms
            FROM {T("recovery_report")}
            ORDER BY ts DESC
            {d.LimitParam("@limit")}
            """;
        cmd.AddParam("@limit", limit);
        var list = new List<RecoveryReportRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new RecoveryReportRecord(
                Id: rd.GetInt64(0),
                Ts: DateTime.ParseExact(rd.GetString(1), IssueStore.DateFormat, CultureInfo.InvariantCulture),
                SpecId: rd.IsDBNull(2) ? null : rd.GetString(2),
                IssuesScanned: rd.GetInt32(3),
                IssuesReplayed: rd.GetInt32(4),
                IssuesFailed: rd.GetInt32(5),
                ActionsJson: rd.GetString(6),
                DurationMs: rd.GetInt32(7)));
        }
        return list;
    }

    public async Task<RecoveryReportRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, ts, spec_id, issues_scanned, issues_replayed, issues_failed, actions_json, duration_ms
            FROM {T("recovery_report")} WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new RecoveryReportRecord(
                Id: rd.GetInt64(0),
                Ts: DateTime.ParseExact(rd.GetString(1), IssueStore.DateFormat, CultureInfo.InvariantCulture),
                SpecId: rd.IsDBNull(2) ? null : rd.GetString(2),
                IssuesScanned: rd.GetInt32(3),
                IssuesReplayed: rd.GetInt32(4),
                IssuesFailed: rd.GetInt32(5),
                ActionsJson: rd.GetString(6),
                DurationMs: rd.GetInt32(7))
            : null;
    }
}