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

    private readonly string _connectionString;

    public MemoryExtractionStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public async Task RecordAsync(ExtractionResult result, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_extraction(
                ts, task_id, source_chars, extracted_count,
                persisted_keys_json, error)
            VALUES($ts, $task, $src, $count, $keys, $err)
            """;
        cmd.Parameters.AddWithValue("$ts", now.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$task", result.IssueId);
        cmd.Parameters.AddWithValue("$src", result.SourceChars);
        cmd.Parameters.AddWithValue("$count", result.ExtractedCount);
        cmd.Parameters.AddWithValue("$keys",
            System.Text.Json.JsonSerializer.Serialize(result.PersistedKeys));
        cmd.Parameters.AddWithValue("$err", (object?)result.Error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// List extraction runs for a single task, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<MemoryExtractionRecord>> ListForTaskAsync(
        string taskId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, task_id, source_chars, extracted_count,
                   persisted_keys_json, error
            FROM memory_extraction
            WHERE task_id = $task
            ORDER BY ts ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
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