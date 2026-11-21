using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class MemoryStore
{
    /// <summary>Durable operator setting and immutable audit receipt commit together.</summary>
    internal async Task RememberAuditedAsync(string key, string body, string actor, string auditPrefix,
        CancellationToken ct)
    {
        RequireMutableMemoryKey(key);
        RequireMutableMemoryKey(auditPrefix);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            throw new ArgumentException("An authenticated operator identity is required.", nameof(actor));
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        // Acquire the write lock before reading, including SQLite's deferred
        // transaction path; SQL Server serializable protects a missing key too.
        using (var acquire = connection.CreateCommand())
        {
            acquire.Transaction = transaction;
            acquire.CommandText = $"UPDATE {T("memory")} SET body=body WHERE [key]=@key";
            acquire.AddParam("@key", key);
            await acquire.ExecuteNonQueryAsync(ct);
        }
        string? before;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT body FROM {T("memory")} WHERE [key]=@key";
            read.AddParam("@key", key);
            before = await read.ExecuteScalarAsync(ct) as string;
        }
        var now = DateTime.UtcNow;
        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = _db.Provider == ForgeDbProvider.SqlServer
                ? $"MERGE {T("memory")} WITH (HOLDLOCK) AS t USING (SELECT @key AS [key]) AS s ON t.[key]=s.[key] WHEN MATCHED THEN UPDATE SET ts=@ts,body=@body,ttl_days=NULL WHEN NOT MATCHED THEN INSERT(ts,[key],body,ttl_days) VALUES(@ts,@key,@body,NULL);"
                : "INSERT INTO memory(ts,[key],body,ttl_days) VALUES(@ts,@key,@body,NULL) ON CONFLICT([key]) DO UPDATE SET ts=@ts,body=@body,ttl_days=NULL";
            write.AddParam("@key", key); write.AddParam("@body", body); write.AddParam("@ts", now.ToString(DateFormat));
            await write.ExecuteNonQueryAsync(ct);
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = $"INSERT INTO {T("memory")}(ts,[key],body,ttl_days) VALUES(@ts,@key,@body,NULL)";
            audit.AddParam("@key", auditPrefix + Guid.NewGuid().ToString("N"));
            audit.AddParam("@body", JsonSerializer.Serialize(new { actor, from = before, to = body, at = new DateTimeOffset(now, TimeSpan.Zero) }));
            audit.AddParam("@ts", now.ToString(DateFormat));
            await audit.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
