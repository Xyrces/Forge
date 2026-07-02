using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace PortHorizon.Agents.Core;

/// <summary>
/// P3.5: append-only log of Groomer runs. One row per run
/// (manual trigger via the dashboard's Groom button, or scheduled
/// via the orchestrator's background service). The dashboard's
/// Groomer timeline reads from this table; the operator can see
/// when each spec was last groomed, how many stories / tasks
/// came out, and any error.
/// </summary>
public sealed class IssueGroomerRunStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public IssueGroomerRunStore(string dbPath)
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

    public async Task<IssueGroomerRun> StartAsync(string specId, GroomerTriggerKind trigger, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO issue_groomer_run(ts, spec_id, trigger_kind, status)
            VALUES($ts, $spec, $trigger, 'started')
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$ts", now.ToString(IssueStore.DateFormat));
        cmd.Parameters.AddWithValue("$spec", specId);
        cmd.Parameters.AddWithValue("$trigger", trigger.ToString().ToLowerInvariant());
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return new IssueGroomerRun(
            Id: id,
            Ts: now,
            SpecId: specId,
            Trigger: trigger,
            Status: GroomerRunStatus.Started,
            StoriesProduced: 0,
            TasksProduced: 0,
            Error: null,
            DurationMs: 0);
    }

    public async Task<IssueGroomerRun> FinishAsync(
        long runId,
        GroomerRunStatus status,
        int storiesProduced,
        int tasksProduced,
        string? error,
        TimeSpan duration,
        CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE issue_groomer_run
            SET status = $status,
                stories_produced = $stories,
                tasks_produced = $tasks,
                error = $error,
                duration_ms = $ms
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$status", StatusToDb(status));
        cmd.Parameters.AddWithValue("$stories", storiesProduced);
        cmd.Parameters.AddWithValue("$tasks", tasksProduced);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", (long)duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$id", runId);
        await cmd.ExecuteNonQueryAsync(ct);
        return new IssueGroomerRun(
            Id: runId,
            Ts: DateTime.MinValue,
            SpecId: "",
            Trigger: GroomerTriggerKind.Manual,
            Status: status,
            StoriesProduced: storiesProduced,
            TasksProduced: tasksProduced,
            Error: error,
            DurationMs: (long)duration.TotalMilliseconds);
    }

    public async Task<IReadOnlyList<IssueGroomerRun>> ListAsync(string? specId = null, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (specId is null)
        {
            cmd.CommandText = """
                SELECT id, ts, spec_id, trigger_kind, status,
                       stories_produced, tasks_produced, error, duration_ms
                FROM issue_groomer_run
                ORDER BY ts DESC
                LIMIT $limit
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT id, ts, spec_id, trigger_kind, status,
                       stories_produced, tasks_produced, error, duration_ms
                FROM issue_groomer_run
                WHERE spec_id = $spec
                ORDER BY ts DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$spec", specId);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<IssueGroomerRun>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new IssueGroomerRun(
                Id: rd.GetInt64(0),
                Ts: DateTime.ParseExact(rd.GetString(1), IssueStore.DateFormat, CultureInfo.InvariantCulture),
                SpecId: rd.GetString(2),
                Trigger: ParseTrigger(rd.GetString(3)),
                Status: ParseStatus(rd.GetString(4)),
                StoriesProduced: rd.GetInt32(5),
                TasksProduced: rd.GetInt32(6),
                Error: rd.IsDBNull(7) ? null : rd.GetString(7),
                DurationMs: rd.GetInt32(8)));
        }
        return list;
    }

    private static string StatusToDb(GroomerRunStatus s) => s switch
    {
        GroomerRunStatus.Started => "started",
        GroomerRunStatus.Succeeded => "succeeded",
        GroomerRunStatus.Failed => "failed",
        _ => "failed",
    };

    private static GroomerRunStatus ParseStatus(string s) => s switch
    {
        "started" => GroomerRunStatus.Started,
        "succeeded" => GroomerRunStatus.Succeeded,
        "failed" => GroomerRunStatus.Failed,
        _ => GroomerRunStatus.Failed,
    };

    private static GroomerTriggerKind ParseTrigger(string s) => s switch
    {
        "manual" => GroomerTriggerKind.Manual,
        "scheduled" => GroomerTriggerKind.Scheduled,
        _ => GroomerTriggerKind.Manual,
    };
}

public enum GroomerTriggerKind { Manual, Scheduled }

public enum GroomerRunStatus { Started, Succeeded, Failed }

public sealed record IssueGroomerRun(
    long Id,
    DateTime Ts,
    string SpecId,
    GroomerTriggerKind Trigger,
    GroomerRunStatus Status,
    int StoriesProduced,
    int TasksProduced,
    string? Error,
    long DurationMs);