using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

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
    IReadOnlyList<string>? Roles = null,
    // Project scope + ownership (schema v24): ProjectId NULL = global
    // (every project's runs see the skill); a project-scoped skill is
    // injected only into runs for that project. Source 'forge' = the
    // row is owned by the operator via the dashboard (edits win over
    // reseeding); Source 'repo' = imported from the project's
    // .kilo/skills directory — the REPO is the source of truth, so the
    // dashboard rejects edits/deletes and the importer overwrites.
    string? ProjectId = null,
    string Source = SkillSources.Forge)
{
    /// <summary>True when the skill applies to every role.</summary>
    public bool IsGlobal => Roles is null || Roles.Count == 0;
}

public static class SkillSources
{
    public const string Forge = "forge";
    public const string Repo = "repo";
}

/// <summary>Thrown when a dashboard caller tries to edit or delete a
/// repo-owned skill. The repo is the source of truth — edit the
/// SKILL.md in the project and let the importer propagate it. Mapped
/// to HTTP 409 by the dashboard endpoints.</summary>
public sealed class RepoOwnedSkillException : InvalidOperationException
{
    public RepoOwnedSkillException(SkillRecord skill)
        : base($"Skill '{skill.Name}' is imported from the {(skill.ProjectId ?? "project")} repository (.kilo/skills) — edit the SKILL.md there; the orchestrator re-imports on startup.")
    {
        SkillId = skill.Id;
    }

    public string SkillId { get; }
}

public sealed record NewSkill(
    string Name, string Body,
    string? Description = null,
    string? AgentId = null,
    bool Enabled = true,
    IReadOnlyList<string>? Roles = null,
    string? ProjectId = null,
    string Source = SkillSources.Forge);

public interface ISkillStore
{
    Task<SkillRecord> CreateAsync(NewSkill spec, CancellationToken ct = default);
    Task<IReadOnlyList<SkillRecord>> ListAsync(string? agentId, bool globalOnly, CancellationToken ct = default);
    /// <summary>Role-scoped listing: <paramref name="role"/> = the
    /// skills whose role set contains that role (global skills are
    /// NOT included); <paramref name="globalOnly"/> = only the
    /// global (empty role set) skills.</summary>
    Task<IReadOnlyList<SkillRecord>> ListByRoleAsync(string? role, bool globalOnly, CancellationToken ct = default);
    /// <summary>Prompt-loading listing (schema v24): every ENABLED-visible
    /// skill a run should see — role set empty OR containing
    /// <paramref name="role"/>, intersect project_id NULL OR equal to
    /// <paramref name="projectId"/> (null projectId = global skills only).
    /// Callers resolve same-name collisions in favor of the
    /// project-scoped copy.</summary>
    Task<IReadOnlyList<SkillRecord>> ListForRunAsync(string role, string? projectId, CancellationToken ct = default);
    Task<SkillRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<SkillRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    /// <summary>Repo-importer reconciliation: delete every
    /// <see cref="SkillSources.Repo"/>-sourced skill for
    /// <paramref name="projectId"/> whose name is NOT in
    /// <paramref name="keepNames"/> (the SKILL.md was removed from the
    /// repo — the repo is the source of truth, so the row must go).
    /// UI-owned rows are never touched. Returns the deleted count.</summary>
    Task<int> DeleteRepoSkillsNotInAsync(string projectId, IReadOnlyCollection<string> keepNames, CancellationToken ct = default);
}

public sealed class SkillStore : ISkillStore, IAsyncDisposable
{
    private readonly IssueStore _issues;
    public SkillStore(IssueStore issues) { _issues = issues; }

    private string T(string name) => _issues.Db.Dialect.Table(name);

    private static string? RolesJson(IReadOnlyList<string>? roles) =>
        roles is null || roles.Count == 0 ? null : JsonSerializer.Serialize(roles);

    private static IReadOnlyList<string> ParseRoles(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<string[]>(json) ?? [];

    public async Task<SkillRecord> CreateAsync(NewSkill spec, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Upsert by (name, project_id) — the catalog is one row per
        // skill per scope (schema v24); the role SET is data on that
        // row, not a key. NULL project_id = the global scope (NULL-safe
        // comparison because SQL Server unique constraints treat NULL
        // as a value but WHERE NULL = NULL never matches). On conflict
        // we keep the existing id and source, and update only the body
        // fields + role set — for repo-sourced rows this is how the
        // importer propagates SKILL.md edits (repo is source of truth).
        var d = _issues.Db.Dialect;
        cmd.CommandText = $"""
            UPDATE {T("skill")}
            SET description=@desc, body=@body, enabled=@enabled, roles=@roles, updated_at=@now
            WHERE name=@name AND ((@pid IS NULL AND project_id IS NULL) OR project_id=@pid);
            INSERT INTO {T("skill")} (id, name, description, body, agent_id, enabled, created_at, updated_at, roles, project_id, source)
            SELECT @id, @name, @desc, @body, @agent, @enabled, @now, @now, @roles, @pid, @source
            WHERE NOT EXISTS (SELECT 1 FROM {T("skill")} WHERE name=@name AND ((@pid IS NULL AND project_id IS NULL) OR project_id=@pid));
            SELECT {d.Top(1)}id FROM {T("skill")} WHERE name=@name AND ((@pid IS NULL AND project_id IS NULL) OR project_id=@pid) ORDER BY created_at DESC{d.Limit(1)}
            """;
        cmd.AddParam("@id", $"skill-{Guid.NewGuid().ToString("N")[..10]}");
        cmd.AddParam("@name", spec.Name);
        cmd.AddParam("@desc", (object?)spec.Description ?? DBNull.Value);
        cmd.AddParam("@body", spec.Body);
        cmd.AddParam("@agent", (object?)spec.AgentId ?? DBNull.Value);
        cmd.AddParam("@roles", (object?)RolesJson(spec.Roles) ?? DBNull.Value);
        cmd.AddParam("@pid", (object?)spec.ProjectId ?? DBNull.Value);
        cmd.AddParam("@source", spec.Source);
        cmd.AddParam("@enabled", spec.Enabled ? 1 : 0);
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
        var id = (string?)await cmd.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Upsert returned no id");
        return await GetAsync(id, ct) ?? throw new InvalidOperationException("Failed to read back skill");
    }

    public async Task<IReadOnlyList<SkillRecord>> ListAsync(string? agentId, bool globalOnly, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sql = SelectSql + " WHERE 1=1";
        if (agentId is not null) sql += " AND agent_id = @agent";
        if (globalOnly) sql += " AND agent_id IS NULL";
        sql += " ORDER BY name";
        cmd.CommandText = sql;
        if (agentId is not null) cmd.AddParam("@agent", agentId);
        var list = new List<SkillRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<IReadOnlyList<SkillRecord>> ListByRoleAsync(string? role, bool globalOnly, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sql = SelectSql + " WHERE 1=1";
        if (role is not null)
            sql += _issues.Db.Provider == ForgeDbProvider.SqlServer
                ? " AND EXISTS (SELECT 1 FROM OPENJSON(skill.roles) WHERE value = @role)"
                : " AND EXISTS (SELECT 1 FROM json_each(skill.roles) WHERE value = @role)";
        if (globalOnly)
            sql += " AND (roles IS NULL OR roles = '[]')";
        sql += " ORDER BY name";
        cmd.CommandText = sql;
        if (role is not null) cmd.AddParam("@role", role);
        var list = new List<SkillRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<IReadOnlyList<SkillRecord>> ListForRunAsync(string role, string? projectId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var rolesMatch = _issues.Db.Provider == ForgeDbProvider.SqlServer
            ? "EXISTS (SELECT 1 FROM OPENJSON(skill.roles) WHERE value = @role)"
            : "EXISTS (SELECT 1 FROM json_each(skill.roles) WHERE value = @role)";
        cmd.CommandText = SelectSql + $"""
             WHERE (skill.roles IS NULL OR skill.roles = '[]' OR {rolesMatch})
               AND (skill.project_id IS NULL OR skill.project_id = @pid)
             ORDER BY name
            """;
        cmd.AddParam("@role", role);
        cmd.AddParam("@pid", (object?)projectId ?? DBNull.Value);
        var list = new List<SkillRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public async Task<SkillRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE id = @id";
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<SkillRecord> UpdateAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var existing = await GetAsync(id, ct) ?? throw new InvalidOperationException($"Skill {id} not found");
        if (existing.Source == SkillSources.Repo)
            throw new RepoOwnedSkillException(existing);
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
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""UPDATE {T("skill")} SET name=@name, description=@desc, body=@body, enabled=@enabled, agent_id=@agent, roles=@roles, updated_at=@now WHERE id=@id""";
        cmd.AddParam("@name", merged.Name);
        cmd.AddParam("@desc", (object?)merged.Description ?? DBNull.Value);
        cmd.AddParam("@body", merged.Body);
        cmd.AddParam("@enabled", merged.Enabled ? 1 : 0);
        cmd.AddParam("@agent", (object?)merged.AgentId ?? DBNull.Value);
        cmd.AddParam("@roles", (object?)RolesJson(merged.Roles) ?? DBNull.Value);
        cmd.AddParam("@now", IssueStore.DateFormatTime(now));
        cmd.AddParam("@id", id);
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
        var existing = await GetAsync(id, ct);
        if (existing?.Source == SkillSources.Repo)
            throw new RepoOwnedSkillException(existing);
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("skill")} WHERE id = @id";
        cmd.AddParam("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteRepoSkillsNotInAsync(string projectId, IReadOnlyCollection<string> keepNames, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {T("skill")} WHERE project_id = @pid AND source = 'repo'";
        cmd.AddParam("@pid", projectId);
        if (keepNames.Count > 0)
        {
            var names = keepNames.Select((_, i) => $"@k{i}").ToList();
            cmd.CommandText += $" AND name NOT IN ({string.Join(",", names)})";
            var i = 0;
            foreach (var n in keepNames) cmd.AddParam($"@k{i++}", n);
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private string SelectSql =>
        $"SELECT id, name, description, body, agent_id, enabled, created_at, updated_at, roles, project_id, source FROM {T("skill")}";

    private static SkillRecord Read(DbDataReader rd) => new(
        Id: rd.GetString(0),
        Name: rd.GetString(1),
        Description: rd.IsDBNull(2) ? null : rd.GetString(2),
        Body: rd.GetString(3),
        AgentId: rd.IsDBNull(4) ? null : rd.GetString(4),
        Enabled: rd.GetInt32(5) != 0,
        CreatedAt: IssueStore.ParseTime(rd.GetString(6)),
        UpdatedAt: IssueStore.ParseTime(rd.GetString(7)),
        Roles: ParseRoles(rd.IsDBNull(8) ? null : rd.GetString(8)),
        ProjectId: rd.IsDBNull(9) ? null : rd.GetString(9),
        Source: rd.IsDBNull(10) ? SkillSources.Forge : rd.GetString(10));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}



