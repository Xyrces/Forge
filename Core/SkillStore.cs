using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Forge.Core;

public sealed record SkillRecord(
    string Id, string Name, string? Description, string Body,
    string? AgentId, bool Enabled,
    DateTime CreatedAt, DateTime UpdatedAt,
    // Role set (schema v23): skills are MANY-TO-MANY — one skill can
    // be given to any set of roles, and each role uses a set of
    // skills. An EMPTY set means GLOBAL (every role sees it). Role
    // names are the canonical names (coredev, clientdev, qa,
    // reviewer, intake). The legacy AgentId column predates the role
    // catalog and is no longer used for resolution.
    IReadOnlyList<string>? Roles = null)
{
    /// <summary>True when the skill applies to every role.</summary>
    public bool IsGlobal => Roles is null || Roles.Count == 0;
}

public sealed record NewSkill(
    string Name, string Body,
    string? Description = null,
    string? AgentId = null,
    bool Enabled = true,
    IReadOnlyList<string>? Roles = null);

public interface ISkillStore
{
    Task<SkillRecord> CreateAsync(NewSkill spec, CancellationToken ct = default);
    Task<IReadOnlyList<SkillRecord>> ListAsync(string? agentId, bool globalOnly, CancellationToken ct = default);
    /// <summary>Role-scoped listing: <paramref name="role"/> = the
    /// skills whose role set contains that role (global skills are
    /// NOT included); <paramref name="globalOnly"/> = only the
    /// global (empty role set) skills.</summary>
    Task<IReadOnlyList<SkillRecord>> ListByRoleAsync(string? role, bool globalOnly, CancellationToken ct = default);
    Task<SkillRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<SkillRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class SkillStore : ISkillStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public SkillStore(IssueStore issues) { _issues = issues; }

    private static string? RolesJson(IReadOnlyList<string>? roles) =>
        roles is null || roles.Count == 0 ? null : JsonSerializer.Serialize(roles);

    private static IReadOnlyList<string> ParseRoles(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<string[]>(json) ?? [];

    public async Task<SkillRecord> CreateAsync(NewSkill spec, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Upsert by name — the catalog is one row per skill (schema
        // v23); the role SET is data on that row, not a key. SQLite
        // generates the id on first insert; on conflict we keep the
        // existing id and update only the body fields + role set.
        cmd.CommandText = @"UPDATE skill
            SET description=$desc, body=$body, enabled=$enabled, roles=$roles, updated_at=$now
            WHERE name=$name;
            INSERT INTO skill (id, name, description, body, agent_id, enabled, created_at, updated_at, roles)
            SELECT $id, $name, $desc, $body, $agent, $enabled, $now, $now, $roles
            WHERE NOT EXISTS (SELECT 1 FROM skill WHERE name=$name);
            SELECT id FROM skill WHERE name=$name ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", $"skill-{Guid.NewGuid().ToString("N")[..10]}");
        cmd.Parameters.AddWithValue("$name", spec.Name);
        cmd.Parameters.AddWithValue("$desc", (object?)spec.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", spec.Body);
        cmd.Parameters.AddWithValue("$agent", (object?)spec.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$roles", (object?)RolesJson(spec.Roles) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", spec.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
        var id = (string?)await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Upsert returned no id");
        return await GetAsync(id, ct) ?? throw new InvalidOperationException("Failed to read back skill");
    }

    public async Task<IReadOnlyList<SkillRecord>> ListAsync(string? agentId, bool globalOnly, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sql = SelectSql + " WHERE 1=1";
        if (agentId is not null) sql += " AND agent_id = $agent";
        if (globalOnly) sql += " AND agent_id IS NULL";
        sql += " ORDER BY name";
        cmd.CommandText = sql;
        if (agentId is not null) cmd.Parameters.AddWithValue("$agent", agentId);
        var list = new List<SkillRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<IReadOnlyList<SkillRecord>> ListByRoleAsync(string? role, bool globalOnly, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sql = SelectSql + " WHERE 1=1";
        if (role is not null)
            sql += " AND EXISTS (SELECT 1 FROM json_each(skill.roles) WHERE value = $role)";
        if (globalOnly)
            sql += " AND (roles IS NULL OR roles = '[]')";
        sql += " ORDER BY name";
        cmd.CommandText = sql;
        if (role is not null) cmd.Parameters.AddWithValue("$role", role);
        var list = new List<SkillRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<SkillRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SkillRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Skill {id} not found");
        var merged = existing with
        {
            Name = fields.TryGetValue("name", out var nm) && nm is string s1 ? s1 : existing.Name,
            Body = fields.TryGetValue("body", out var bd) && bd is string s2 ? s2 : existing.Body,
            Description = fields.TryGetValue("description", out var dsc) ? (string?)dsc : existing.Description,
            Enabled = fields.TryGetValue("enabled", out var en) ? Convert.ToBoolean(en) : existing.Enabled,
            AgentId = fields.TryGetValue("agentId", out var ag) ? (string?)ag : existing.AgentId,
            Roles = ParseRolesField(fields, existing.Roles),
        };
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE skill SET name=$name, description=$desc, body=$body, enabled=$enabled, agent_id=$agent, roles=$roles, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$name", merged.Name);
        cmd.Parameters.AddWithValue("$desc", (object?)merged.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", merged.Body);
        cmd.Parameters.AddWithValue("$enabled", merged.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$agent", (object?)merged.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$roles", (object?)RolesJson(merged.Roles) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", IssueStore.DateFormatTime(now));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        return merged with { UpdatedAt = now };
    }

    private static IReadOnlyList<string>? ParseRolesField(IReadOnlyDictionary<string, object?> fields, IReadOnlyList<string>? existing)
    {
        if (!fields.TryGetValue("roles", out var ro)) return existing;
        return ro switch
        {
            null => [],
            JsonElement { ValueKind: JsonValueKind.Array } arr => arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
            JsonElement { ValueKind: JsonValueKind.Null } => [],
            IEnumerable<string> list => list.ToList(),
            string json => ParseRoles(json),
            _ => existing,
        };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skill WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string SelectSql =
        "SELECT id, name, description, body, agent_id, enabled, created_at, updated_at, roles FROM skill";

    private static SkillRecord Read(SqliteDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        Description: rd.IsDBNull(2) ? null : rd.GetString(2),
        Body: rd.GetString(3),
        AgentId: rd.IsDBNull(4) ? null : rd.GetString(4),
        Enabled: rd.GetInt32(5) != 0,
        CreatedAt: IssueStore.ParseTime(rd.GetString(6)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(7)),
        Roles: ParseRoles(rd.IsDBNull(8) ? null : rd.GetString(8)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}



