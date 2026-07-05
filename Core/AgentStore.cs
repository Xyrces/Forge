using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

public sealed record AgentRecord(
    string Id, string KiloName, string DisplayName, string Scope,
    string? Description, bool Enabled, string ConfigJson,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record NewAgent(
    string KiloName, string DisplayName, string Scope = "",
    string? Description = null, bool Enabled = true,
    IReadOnlyDictionary<string, object>? Config = null);

public interface IAgentStore
{
    Task<AgentRecord> CreateAsync(NewAgent spec, CancellationToken ct = default);
    Task<IReadOnlyList<AgentRecord>> ListAsync(CancellationToken ct = default);
    Task<AgentRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<AgentRecord?> GetByKiloNameAsync(string kiloName, CancellationToken ct = default);
    Task<AgentRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task BulkUpsertFromKiloFilesAsync(IEnumerable<(string KiloName, string DisplayName, string Scope, string? Description)> entries, CancellationToken ct = default);
}

public sealed class AgentStore : IAgentStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public AgentStore(IssueStore issues) { _issues = issues; }

    public async Task<AgentRecord> CreateAsync(NewAgent spec, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var id = $"agent-{Guid.NewGuid().ToString("N")[..10]}";
        var now = DateTime.UtcNow;
        var configJson = JsonSerializer.Serialize(spec.Config ?? new Dictionary<string, object>());
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = @"INSERT INTO agent (id, kilo_name, display_name, scope, description, enabled, config_json, created_at, updated_at)
                VALUES ($id, $kilo, $display, $scope, $desc, $enabled, $config, $now, $now)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kilo", spec.KiloName);
            cmd.Parameters.AddWithValue("$display", spec.DisplayName);
            cmd.Parameters.AddWithValue("$scope", spec.Scope ?? "");
            cmd.Parameters.AddWithValue("$desc", (object?)spec.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enabled", spec.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$config", configJson);
            cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new AgentRecord(id, spec.KiloName, spec.DisplayName, spec.Scope ?? "", spec.Description, spec.Enabled, configJson, now, now);
    }

    public async Task<IReadOnlyList<AgentRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kilo_name, display_name, scope, description, enabled, config_json, created_at, updated_at FROM agent ORDER BY display_name";
        var list = new List<AgentRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<AgentRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kilo_name, display_name, scope, description, enabled, config_json, created_at, updated_at FROM agent WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<AgentRecord?> GetByKiloNameAsync(string kiloName, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kilo_name, display_name, scope, description, enabled, config_json, created_at, updated_at FROM agent WHERE kilo_name = $kilo";
        cmd.Parameters.AddWithValue("$kilo", kiloName);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<AgentRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Agent {id} not found");
        var merged = existing with
        {
            KiloName = fields.TryGetValue("kiloName", out var kn) && kn is string s1 ? s1 : existing.KiloName,
            DisplayName = fields.TryGetValue("displayName", out var dn) && dn is string s2 ? s2 : existing.DisplayName,
            Scope = fields.TryGetValue("scope", out var sc) && sc is string s3 ? s3 : existing.Scope,
            Description = fields.TryGetValue("description", out var dsc) ? (string?)dsc : existing.Description,
            Enabled = fields.TryGetValue("enabled", out var en) ? Convert.ToBoolean(en) : existing.Enabled,
            ConfigJson = fields.TryGetValue("config", out var cfgObj) && cfgObj is IReadOnlyDictionary<string, object> cfg
                ? JsonSerializer.Serialize(new Dictionary<string, object>(cfg))
                : existing.ConfigJson,
        };
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE agent SET kilo_name=$kilo, display_name=$display, scope=$scope, description=$desc, enabled=$enabled, config_json=$config, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$kilo", merged.KiloName);
        cmd.Parameters.AddWithValue("$display", merged.DisplayName);
        cmd.Parameters.AddWithValue("$scope", merged.Scope);
        cmd.Parameters.AddWithValue("$desc", (object?)merged.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", merged.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$config", merged.ConfigJson);
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        return merged with { UpdatedAt = now };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task BulkUpsertFromKiloFilesAsync(IEnumerable<(string KiloName, string DisplayName, string Scope, string? Description)> entries, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var entry in entries)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = @"INSERT INTO agent (id, kilo_name, display_name, scope, description, enabled, config_json, created_at, updated_at)
                VALUES ($id, $kilo, $display, $scope, $desc, 1, '{}', $now, $now)
                ON CONFLICT(kilo_name) DO UPDATE SET
                    display_name = excluded.display_name,
                    scope        = excluded.scope,
                    description  = excluded.description,
                    updated_at   = excluded.updated_at";
            cmd.Parameters.AddWithValue("$id", $"agent-{Guid.NewGuid().ToString("N")[..10]}");
            cmd.Parameters.AddWithValue("$kilo", entry.KiloName);
            cmd.Parameters.AddWithValue("$display", entry.DisplayName);
            cmd.Parameters.AddWithValue("$scope", entry.Scope);
            cmd.Parameters.AddWithValue("$desc", (object?)entry.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(DateTime.UtcNow));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static AgentRecord Read(SqliteDataReader rd) => new(
        Id: rd.GetString(0),
        KiloName: rd.GetString(1),
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

