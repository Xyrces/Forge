using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record AgentRecord(
    string Id, string AgentName, string DisplayName, string Scope,
    string? Description, bool Enabled, string ConfigJson,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record NewAgent(
    string AgentName, string DisplayName, string Scope = "",
    string? Description = null, bool Enabled = true,
    IReadOnlyDictionary<string, object>? Config = null);

public interface IAgentStore
{
    Task<AgentRecord> CreateAsync(NewAgent spec, CancellationToken ct = default);
    Task<IReadOnlyList<AgentRecord>> ListAsync(CancellationToken ct = default);
    Task<AgentRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<AgentRecord?> GetByNameAsync(string agentName, CancellationToken ct = default);
    Task<AgentRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task BulkUpsertFromAgentFilesAsync(IEnumerable<(string AgentName, string DisplayName, string Scope, string? Description)> entries, CancellationToken ct = default);
}

public sealed class AgentStore : IAgentStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public AgentStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    public async Task<AgentRecord> CreateAsync(NewAgent spec, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = $"agent-{Guid.NewGuid().ToString("N")[..10]}";
        var now = DateTime.UtcNow;
        var configJson = JsonSerializer.Serialize(spec.Config ?? new Dictionary<string, object>());
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("agent")} (id, agent_name, display_name, scope, description, enabled, config_json, created_at, updated_at)
                VALUES (@id, @agent, @display, @scope, @desc, @enabled, @config, @now, @now)
                """;
            cmd.AddParam("@id", id);
            cmd.AddParam("@agent", spec.AgentName);
            cmd.AddParam("@display", spec.DisplayName);
            cmd.AddParam("@scope", spec.Scope ?? "");
            cmd.AddParam("@desc", (object?)spec.Description ?? DBNull.Value);
            cmd.AddParam("@enabled", spec.Enabled ? 1 : 0);
            cmd.AddParam("@config", configJson);
            cmd.AddParam("@now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new AgentRecord(id, spec.AgentName, spec.DisplayName, spec.Scope ?? "", spec.Description, spec.Enabled, configJson, now, now);
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, agent_name, display_name, scope, description, enabled, config_json, created_at, updated_at FROM {T("agent")} ORDER BY display_name";
        var list = new List<AgentRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<AgentRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, agent_name, display_name, scope, description, enabled, config_json, created_at, updated_at FROM {T("agent")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<AgentRecord?> GetByNameAsync(string agentName, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, agent_name, display_name, scope, description, enabled, config_json, created_at, updated_at FROM {T("agent")} WHERE agent_name = @agent";
        cmd.AddParam("@agent", agentName);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<AgentRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Agent {id} not found");
        var merged = existing with
        {
            AgentName = fields.TryGetValue("agentName", out var kn) && kn is string s1 ? s1 : existing.AgentName,
            DisplayName = fields.TryGetValue("displayName", out var dn) && dn is string s2 ? s2 : existing.DisplayName,
            Scope = fields.TryGetValue("scope", out var sc) && sc is string s3 ? s3 : existing.Scope,
            Description = fields.TryGetValue("description", out var dsc) ? (string?)dsc : existing.Description,
            Enabled = fields.TryGetValue("enabled", out var en) ? Convert.ToBoolean(en) : existing.Enabled,
            ConfigJson = fields.TryGetValue("config", out var cfgObj) && cfgObj is IReadOnlyDictionary<string, object> cfg
                ? JsonSerializer.Serialize(new Dictionary<string, object>(cfg))
                : existing.ConfigJson,
        };
        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""UPDATE {T("agent")} SET agent_name=@agent, display_name=@display, scope=@scope, description=@desc, enabled=@enabled, config_json=@config, updated_at=@now WHERE id=@id""";
        cmd.AddParam("@agent", merged.AgentName);
        cmd.AddParam("@display", merged.DisplayName);
        cmd.AddParam("@scope", merged.Scope);
        cmd.AddParam("@desc", (object?)merged.Description ?? DBNull.Value);
        cmd.AddParam("@enabled", merged.Enabled ? 1 : 0);
        cmd.AddParam("@config", merged.ConfigJson);
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        return merged with { UpdatedAt = now };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("agent")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task BulkUpsertFromAgentFilesAsync(IEnumerable<(string AgentName, string DisplayName, string Scope, string? Description)> entries, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var entry in entries)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = _issues.Db.Provider == ForgeDbProvider.SqlServer
                ? $$"""
                    MERGE {{T("agent")}} WITH (HOLDLOCK) AS t
                    USING (SELECT @agent AS agent_name) AS s ON t.agent_name = s.agent_name
                    WHEN MATCHED THEN UPDATE SET display_name = @display, scope = @scope, description = @desc, updated_at = @now
                    WHEN NOT MATCHED THEN INSERT (id, agent_name, display_name, scope, description, enabled, config_json, created_at, updated_at)
                        VALUES (@id, @agent, @display, @scope, @desc, 1, '{}', @now, @now);
                    """
                : """
                    INSERT INTO agent (id, agent_name, display_name, scope, description, enabled, config_json, created_at, updated_at)
                    VALUES (@id, @agent, @display, @scope, @desc, 1, '{}', @now, @now)
                    ON CONFLICT(agent_name) DO UPDATE SET
                        display_name = excluded.display_name,
                        scope        = excluded.scope,
                        description  = excluded.description,
                        updated_at   = excluded.updated_at
                    """;
            cmd.AddParam("@id", $"agent-{Guid.NewGuid().ToString("N")[..10]}");
            cmd.AddParam("@agent", entry.AgentName);
            cmd.AddParam("@display", entry.DisplayName);
            cmd.AddParam("@scope", entry.Scope);
            cmd.AddParam("@desc", (object?)entry.Description ?? DBNull.Value);
            cmd.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static AgentRecord Read(DbDataReader rd) => new(
        Id: rd.GetString(0),
        AgentName: rd.GetString(1),
        DisplayName: rd.GetString(2),
        Scope: rd.GetString(3),
        Description: rd.IsDBNull(4) ? null : rd.GetString(4),
        Enabled: rd.GetInt32(5) != 0,
        ConfigJson: rd.GetString(6),
        CreatedAt: IssueStore.ParseTime(rd.GetString(7)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(8)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}

