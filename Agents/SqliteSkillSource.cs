using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// SQLite-backed skill source. Loads global + role-scoped skills from
/// <see cref="ISkillStore"/> keyed by the canonical role NAME
/// (schema v22 <c>skill.role</c>) — the legacy AgentRecord
/// indirection is gone (the agent table is empty in practice).
/// </summary>
public sealed class SqliteSkillSource : ISkillSource
{
    private readonly ISkillStore _skills;
    private readonly RoleAgentRegistry _roles;

    public SqliteSkillSource(ISkillStore skills, RoleAgentRegistry roles)
    {
        _skills = skills;
        _roles = roles;
    }

    public async Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(AgentType role, CancellationToken ct = default)
    {
        var roleName = _roles.ForType(role).AgentName;

        var globals = await _skills.ListByRoleAsync(role: null, globalOnly: true, ct);
        var roleSkills = await _skills.ListByRoleAsync(role: roleName, globalOnly: false, ct);

        // Merge + filter enabled. Global first, then role-specific, both
        // sorted by name. Duplicates (same name in global + role) are
        // resolved in favor of the role-specific copy.
        var byName = new Dictionary<string, SkillContent>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in globals)
        {
            if (!s.Enabled) continue;
            byName[s.Name] = new SkillContent(s.Name, s.Description, s.Body);
        }
        foreach (var s in roleSkills)
        {
            if (!s.Enabled) continue;
            byName[s.Name] = new SkillContent(s.Name, s.Description, s.Body);
        }
        return byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Test-only skill source that returns a pre-built list. Use this in
/// unit tests to avoid spinning up SQLite.
/// </summary>
public sealed class InMemorySkillSource : ISkillSource
{
    private readonly Dictionary<AgentType, IReadOnlyList<SkillContent>> _byRole;

    public InMemorySkillSource(Dictionary<AgentType, IReadOnlyList<SkillContent>> byRole)
    {
        _byRole = byRole;
    }

    public Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(AgentType role, CancellationToken ct = default)
        => Task.FromResult(_byRole.TryGetValue(role, out var list)
            ? list
            : (IReadOnlyList<SkillContent>)Array.Empty<SkillContent>());
}
