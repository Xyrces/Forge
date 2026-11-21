using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record TaskHistoryPage(IReadOnlyList<IssueEventRecord> Events, string? NextCursor);

/// <summary>Stable, bounded keyset reads of the owning task's event stream.</summary>
public sealed class TaskHistoryReadStore(IDbConnectionFactory db)
{
    private sealed record Cursor(string Scope, string At, long Id);
    public async Task<TaskHistoryPage> ReadAsync(string scope, string taskId, string? cursor, CancellationToken ct = default)
    {
        Cursor? before = null;
        if (cursor is not null)
        {
            try { before = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor)); }
            catch (Exception ex) when (ex is FormatException or JsonException) { throw new ArgumentException("Invalid history cursor.", nameof(cursor)); }
            if (before?.Scope != scope + ":" + taskId) throw new ArgumentException("Cursor belongs to another task.", nameof(cursor));
        }
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {db.Dialect.Top(101)}id,issue_id,ts,actor,kind,detail FROM {db.Dialect.Table("issue_event")} WHERE issue_id=@task"
            + (before is null ? "" : " AND (ts<@at OR (ts=@at AND id<@id))")
            + $" ORDER BY ts DESC,id DESC{db.Dialect.Limit(101)}";
        command.AddParam("@task", taskId);
        if (before is not null) { command.AddParam("@at", before.At); command.AddParam("@id", before.Id); }
        var rows = new List<IssueEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetInt64(0), reader.GetString(1), IssueStore.ParseTime(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        var page = rows.Take(100).ToArray();
        var next = rows.Count > 100 ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(scope + ":" + taskId, IssueStore.DateFormatTime(page[^1].Timestamp), page[^1].Id))) : null;
        return new(page, next);
    }
}
