using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Forge.Core;

/// <summary>
/// Persistent project memory. Stores keyed insights that survive
/// across processes and get injected into agent prompts (the
/// <c>bd remember</c> / <c>bd prime</c> analog from
/// <c>docs/embedded-issues.md</c> Phase 3).
///
/// <para>
/// Schema lives in <see cref="IssueStore"/>'s initializer (v7). This
/// class is the typed access layer; tests assert CRUD + TTL semantics.
/// </para>
///
/// <para>
/// TTL semantics: rows with <c>ttl_days</c> set are returned by
/// <see cref="RecallAsync"/> only if their stored timestamp + N days is
/// in the future. Expired rows are <i>not</i> deleted by
/// <see cref="RecallAsync"/>; they're filtered out of the result set.
/// A background sweep (out of scope for v1) would purge them.
/// </para>
/// </summary>
public sealed class MemoryStore : IAsyncDisposable
{
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly IDbConnectionFactory _db;
    private readonly string _dbPath;

    public MemoryStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
        _dbPath = dbPath;
    }

    public MemoryStore(IDbConnectionFactory db)
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

    /// <summary>
    /// Upsert by <paramref name="key"/>. Returns the row after the write.
    /// If <paramref name="ttlDays"/> is null, the row never expires;
    /// otherwise it auto-decays N days after this call's timestamp.
    /// </summary>
    public async Task<MemoryRecord> RememberAsync(
        string key, string body, int? ttlDays = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key required", nameof(key));
        if (body is null) throw new ArgumentNullException(nameof(body));

        var now = DateTime.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                MERGE {T("memory")} WITH (HOLDLOCK) AS t
                USING (SELECT @key AS [key]) AS s ON t.[key] = s.[key]
                WHEN MATCHED THEN UPDATE SET ts = @ts, body = @body, ttl_days = @ttl
                WHEN NOT MATCHED THEN INSERT (ts, [key], body, ttl_days) VALUES (@ts, @key, @body, @ttl);
                """
            : """
                INSERT INTO memory(ts, [key], body, ttl_days)
                VALUES(@ts, @key, @body, @ttl)
                ON CONFLICT([key]) DO UPDATE SET
                    ts = @ts,
                    body = @body,
                    ttl_days = @ttl
                """;
        cmd.AddParam("@ts", now.ToString(DateFormat));
        cmd.AddParam("@key", key);
        cmd.AddParam("@body", body);
        cmd.AddParam("@ttl", (object?)ttlDays ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        var expiresAt = ttlDays is null ? (DateTime?)null : now.AddDays(ttlDays.Value);
        return new MemoryRecord(Id: 0, Key: key, Body: body, CreatedAt: now, TtlDays: ttlDays, ExpiresAt: expiresAt);
    }

    /// <summary>
    /// Idempotently seeds a memory key. If the key already exists
    /// (and is not expired), the existing record is returned and
    /// <paramref name="body"/> is NOT written. This protects
    /// operator edits from being overwritten by orchestrator restart.
    /// </summary>
    public async Task<MemoryRecord> SeedIfMissingAsync(
        string key, string body, int? ttlDays = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key required", nameof(key));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        // LIKE match: the key column is unique, so a "playbook/repo"
        // lookup returns either zero or one row. We do an exact match
        // by quoting the value so '%' characters in the key don't
        // act as wildcards.
        var existing = await RecallAsync(keyPrefix: null, ct);
        var hit = existing.FirstOrDefault(m =>
            string.Equals(m.Key, key, StringComparison.Ordinal));
        if (hit is not null)
        {
            return hit;
        }
        return await RememberAsync(key, body, ttlDays, ct);
    }

    /// <summary>
    /// Read all non-expired memories. If <paramref name="keyPrefix"/>
    /// is null, returns everything; otherwise filters by LIKE prefix.
    /// </summary>
    public async Task<IReadOnlyList<MemoryRecord>> RecallAsync(
        string? keyPrefix = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, ts, [key], body, ttl_days
            FROM {T("memory")}
            WHERE (@prefix IS NULL OR [key] LIKE @prefixPattern)
            ORDER BY ts ASC
            """;
        cmd.AddParam("@prefix", (object?)keyPrefix ?? DBNull.Value);
        cmd.AddParam("@prefixPattern",
            keyPrefix is null ? (object)DBNull.Value : keyPrefix + "%");
        var list = new List<MemoryRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var now = DateTime.UtcNow;
        while (await rd.ReadAsync(ct))
        {
            var ttl = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4);
            var createdAt = ParseDate(rd.GetString(1));
            var expiresAt = ttl is null ? (DateTime?)null : createdAt.AddDays(ttl.Value);
            // Skip expired rows; do not delete.
            if (expiresAt is { } exp && exp <= now) continue;
            list.Add(new MemoryRecord(
                Id: rd.GetInt64(0),
                Key: rd.GetString(2),
                Body: rd.GetString(3),
                CreatedAt: createdAt,
                TtlDays: ttl,
                ExpiresAt: expiresAt));
        }
        return list;
    }

    /// <summary>
    /// Forget a memory by exact key. Returns true if a row was deleted.
    /// </summary>
    public async Task<bool> ForgetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("memory")} WHERE [key] = @key";
        cmd.AddParam("@key", key);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    /// <summary>
    /// Delete expired rows. Optional maintenance; not called automatically.
    /// </summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Provider-neutral: select TTL rows, filter expiry in C#,
        // delete by id. Purge is a rare manual maintenance call, so
        // the extra round-trip is fine and avoids provider-specific
        // date arithmetic on string-typed timestamps.
        var expiredIds = new List<long>();
        var now = DateTime.UtcNow;
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = $"SELECT id, ts, ttl_days FROM {T("memory")} WHERE ttl_days IS NOT NULL";
            await using var rd = await read.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var createdAt = ParseDate(rd.GetString(1));
                if (createdAt.AddDays(rd.GetInt32(2)) <= now)
                    expiredIds.Add(rd.GetInt64(0));
            }
        }
        if (expiredIds.Count == 0) return 0;
        var names = expiredIds.Select((_, i) => $"@id{i}").ToArray();
        cmd.CommandText = $"DELETE FROM {T("memory")} WHERE id IN ({string.Join(",", names)})";
        for (var i = 0; i < expiredIds.Count; i++) cmd.AddParam(names[i], expiredIds[i]);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Build the prompt-injection block. Returns an empty string if
    /// there are no memories (so callers can always concatenate).
    /// </summary>
    public static string RenderForPrompt(IReadOnlyList<MemoryRecord> memories)
        => RenderSectionForPrompt("## Project memory", memories);

    /// <summary>
    /// Render one memory section with a caller-chosen header. Used
    /// for the sprint-scoped block ("## Sprint memory") alongside the
    /// global project block.
    /// </summary>
    public static string RenderSectionForPrompt(string header, IReadOnlyList<MemoryRecord> memories)
    {
        if (memories.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine("Persistent insights from past work. Apply where relevant; ");
        sb.AppendLine("do not quote verbatim unless the task asks.");
        sb.AppendLine();
        foreach (var m in memories)
        {
            sb.Append("- **").Append(m.Key).Append("**");
            if (m.ExpiresAt is { } exp)
                sb.Append(" _(expires ").Append(exp.ToString("u")).Append(")_");
            sb.AppendLine(":");
            sb.AppendLine($"  {m.Body}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static DateTime ParseDate(string s) => DateTime.ParseExact(s, DateFormat, System.Globalization.CultureInfo.InvariantCulture);

    public string ConnectionString => _db.ConnectionString;

    /// <summary>Connection factory (migration tooling reads raw rows).</summary>
    public IDbConnectionFactory Db => _db;

    public async ValueTask DisposeAsync()
    {
        // Pooled connections; nothing to dispose.
        await ValueTask.CompletedTask;
    }

    public void Dispose() { /* pooled connections */ }
}

public sealed record MemoryRecord(
    long Id,
    string Key,
    string Body,
    DateTime CreatedAt,
    int? TtlDays,
    DateTime? ExpiresAt);