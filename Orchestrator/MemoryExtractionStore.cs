using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>
/// P5.5: audit-log access for auto-extracted project memory.
/// The store of record is <see cref="MemoryStore"/>; this class
/// records the extraction runs so the operator can audit what
/// the LLM pulled from each model response and which keys
/// landed in the memory table.
///
/// <para>
/// Schema lives in <see cref="IssueStore"/>'s initializer (v13).
/// This class is the typed access layer.
/// </para>
/// </summary>
public sealed class MemoryExtractionStore : IAsyncDisposable
{
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly IDbConnectionFactory _db;

    public MemoryExtractionStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
    }

    public MemoryExtractionStore(IDbConnectionFactory db)
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

    public async Task RecordAsync(ExtractionResult result, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {T("memory_extraction")}(
                ts, task_id, source_chars, extracted_count,
                persisted_keys_json, error)
            VALUES(@ts, @task, @src, @count, @keys, @err)
            """;
        cmd.AddParam("@ts", now.ToString(DateFormat));
        cmd.AddParam("@task", result.IssueId);
        cmd.AddParam("@src", result.SourceChars);
        cmd.AddParam("@count", result.ExtractedCount);
        cmd.AddParam("@keys",
            System.Text.Json.JsonSerializer.Serialize(result.PersistedKeys));
        cmd.AddParam("@err", (object?)result.Error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// List extraction runs for a single task, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<MemoryExtractionRecord>> ListForTaskAsync(
        string taskId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, ts, task_id, source_chars, extracted_count,
                   persisted_keys_json, error
            FROM {T("memory_extraction")}
            WHERE task_id = @task
            ORDER BY ts ASC, id ASC
            """;
        cmd.AddParam("@task", taskId);
        var list = new List<MemoryExtractionRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new MemoryExtractionRecord(
                Id: rd.GetInt64(0),
                Timestamp: DateTime.ParseExact(
                    rd.GetString(1), DateFormat,
                    System.Globalization.CultureInfo.InvariantCulture),
                TaskId: rd.GetString(2),
                SourceChars: rd.GetInt32(3),
                ExtractedCount: rd.GetInt32(4),
                PersistedKeys: System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    rd.GetString(5)) ?? new List<string>(),
                Error: rd.IsDBNull(6) ? null : rd.GetString(6)));
        }
        return list;
    }

    /// <summary>
    /// List the most recent extraction runs across all tasks,
    /// newest first. Used by <c>GET /api/memory/extractions</c>
    /// to feed the global Memory Core view.
    /// </summary>
    public async Task<IReadOnlyList<MemoryExtractionRecord>> ListAsync(
        int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var d = _db.Dialect;
        cmd.CommandText = $"""
            SELECT {d.TopParam("@limit")}id, ts, task_id, source_chars, extracted_count,
                   persisted_keys_json, error
            FROM {T("memory_extraction")}
            ORDER BY ts DESC, id DESC
            {d.LimitParam("@limit")}
            """;
        cmd.AddParam("@limit", limit);
        var list = new List<MemoryExtractionRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new MemoryExtractionRecord(
                Id: rd.GetInt64(0),
                Timestamp: DateTime.ParseExact(
                    rd.GetString(1), DateFormat,
                    System.Globalization.CultureInfo.InvariantCulture),
                TaskId: rd.GetString(2),
                SourceChars: rd.GetInt32(3),
                ExtractedCount: rd.GetInt32(4),
                PersistedKeys: System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                    rd.GetString(5)) ?? new List<string>(),
                Error: rd.IsDBNull(6) ? null : rd.GetString(6)));
        }
        return list;
    }

    public async ValueTask DisposeAsync() => await ValueTask.CompletedTask;
    public void Dispose() { }
}

public sealed record MemoryExtractionRecord(
    long Id,
    DateTime Timestamp,
    string TaskId,
    int SourceChars,
    int ExtractedCount,
    IReadOnlyList<string> PersistedKeys,
    string? Error);