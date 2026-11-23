using System.Text.Json.Nodes;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Atomic, sibling-preserving settings updates in the operator-owned registry.</summary>
internal sealed class ProjectSettingsJson(IssueStore issues)
{
    public async Task<(bool Exists, string? Raw, JsonObject Value)> ReadAsync(string id, CancellationToken ct)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT roles_json FROM {issues.Db.Dialect.Table("project")} WHERE id=@id";
        command.AddParam("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, null, new());
        var raw = reader.IsDBNull(0) ? null : reader.GetString(0);
        // Refuse corrupt settings rather than accidentally erase an image approval.
        var value = raw is null ? new JsonObject() : JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException("Project settings must be a JSON object.");
        return (true, raw, value);
    }

    public async Task<bool> MutateAsync(string id, Action<JsonObject> mutate, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var previous = await ReadAsync(id, ct);
            if (!previous.Exists) return false;
            mutate(previous.Value);
            await using var connection = await issues.Db.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            var equality = issues.Db.Provider == ForgeDbProvider.SqlServer
                ? "CONVERT(varbinary(max),roles_json)=CONVERT(varbinary(max),@before)"
                : "roles_json COLLATE BINARY=@before";
            command.CommandText = $"UPDATE {issues.Db.Dialect.Table("project")} SET roles_json=@after, updated_at=@now WHERE id=@id AND ((roles_json IS NULL AND @before IS NULL) OR {equality})";
            command.AddParam("@id", id);
            command.AddParam("@before", previous.Raw);
            command.AddParam("@after", previous.Value.ToJsonString());
            command.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            if (await command.ExecuteNonQueryAsync(ct) == 1) return true;
        }
        throw new ProjectSettingsConflictException("Project settings changed concurrently; reload and retry.");
    }
}

public sealed class ProjectSettingsConflictException(string message) : InvalidOperationException(message);
