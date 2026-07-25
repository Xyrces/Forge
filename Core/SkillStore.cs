using Microsoft.Data.Sqlite;

namespace Forge.Core;

public sealed record SkillRecord(
    string Id, string Name, string? Description, string Body,
    string? AgentId, bool Enabled,
    DateTime CreatedAt, DateTime UpdatedAt,
    // Role-name scope (schema v22): NULL = global (every agent sees
    // it); otherwise the canonical role name (coredev, clientdev,
    // qa, reviewer, intake). The legacy AgentId column predates the
    // canonical role catalog and is no longer used for resolution.
    string? Role = null);

public sealed record NewSkill(
    string Name, string Body,
    string? Description = null,
    string? AgentId = null,
    bool Enabled = true,
    string? Role = null);

public interface ISkillStore
{
    Task<SkillRecord> CreateAsync(NewSkill spec, CancellationToken ct = default);
    Task<IReadOnlyList<SkillRecord>> ListAsync(string? agentId, bool globalOnly, CancellationToken ct = default);
    /// <summary>Role-scoped listing: <paramref name="role"/> = one
    /// role's skills; <paramref name="globalOnly"/> = only the
    /// global (role IS NULL) set.</summary>
    Task<IReadOnlyList<SkillRecord>> ListByRoleAsync(string? role, bool globalOnly, CancellationToken ct = default);
    Task<SkillRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<SkillRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class SkillStore : ISkillStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public SkillStore(IssueStore issues) { _issues = issues; }

    public async Task<SkillRecord> CreateAsync(NewSkill spec, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Upsert by (name, role). SQLite generates the id on first insert;
        // on conflict we keep the existing id and update only the body fields.
        cmd.CommandText = @"UPDATE skill
            SET description=$desc, body=$body, enabled=$enabled, updated_at=$now
            WHERE name=$name AND ((role IS NULL AND $role IS NULL) OR role=$role);
            INSERT INTO skill (id, name, description, body, agent_id, enabled, created_at, updated_at, role)
            SELECT $id, $name, $desc, $body, $agent, $enabled, $now, $now, $role
            WHERE NOT EXISTS (SELECT 1 FROM skill WHERE name=$name AND ((role IS NULL AND $role IS NULL) OR role=$role));
            SELECT id FROM skill WHERE name=$name AND ((role IS NULL AND $role IS NULL) OR role=$role) ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", $"skill-{Guid.NewGuid().ToString("N")[..10]}");
        cmd.Parameters.AddWithValue("$name", spec.Name);
        cmd.Parameters.AddWithValue("$desc", (object?)spec.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", spec.Body);
        cmd.Parameters.AddWithValue("$agent", (object?)spec.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", (object?)spec.Role ?? DBNull.Value);
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
        var sql = "SELECT id, name, description, body, agent_id, enabled, created_at, updated_at, role FROM skill WHERE 1=1";
        if (agentId is not null) sql += " AND agent_id = $agent";
        if (globalOnly) sql += " AND agent_id IS NULL";
        sql += " ORDER BY agent_id NULLS FIRST, name";
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
        var sql = "SELECT id, name, description, body, agent_id, enabled, created_at, updated_at, role FROM skill WHERE 1=1";
        if (role is not null) sql += " AND role = $role";
        if (globalOnly) sql += " AND role IS NULL";
        sql += " ORDER BY role NULLS FIRST, name";
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
        cmd.CommandText = "SELECT id, name, description, body, agent_id, enabled, created_at, updated_at, role FROM skill WHERE id = $id";
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
            Role = fields.TryGetValue("role", out var ro) ? (string?)ro : existing.Role,
        };
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_issues.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE skill SET name=$name, description=$desc, body=$body, enabled=$enabled, agent_id=$agent, role=$role, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$name", merged.Name);
        cmd.Parameters.AddWithValue("$desc", (object?)merged.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", merged.Body);
        cmd.Parameters.AddWithValue("$enabled", merged.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$agent", (object?)merged.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", (object?)merged.Role ?? DBNull.Value);
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
        cmd.CommandText = "DELETE FROM skill WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SkillRecord Read(SqliteDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        Description: rd.IsDBNull(2) ? null : rd.GetString(2),
        Body: rd.GetString(3),
        AgentId: rd.IsDBNull(4) ? null : rd.GetString(4),
        Enabled: rd.GetInt32(5) != 0,
        CreatedAt: IssueStore.ParseTime(rd.GetString(6)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(7)),
        Role: rd.IsDBNull(8) ? null : rd.GetString(8));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}



