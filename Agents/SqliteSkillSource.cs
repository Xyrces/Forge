using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// SQLite-backed skill source. Loads global + role-scoped skills from
/// <see cref="ISkillStore"/> (schema v23 <c>skill.roles</c> JSON
/// array): a role sees every GLOBAL skill (empty role set) plus every
/// skill whose role set contains its canonical name. Skills are
/// many-to-many — the same skill row can be given to any set of
/// roles. The legacy AgentRecord indirection is gone (the agent table
/// is empty in practice).
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

    public async Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(AgentType role, string? projectId = null, CancellationToken ct = default)
    {
        var roleName = _roles.ForType(role).AgentName;

        // One scoped query: role match (empty set or contains the role)
        // intersect project match (global or this project). Merge by
        // name with a deterministic rank: a project-scoped copy beats a
        // global one (projects refine global skills), and a role-scoped
        // copy beats a role-global one (role rows refine shared rows).
        var rows = await _skills.ListForRunAsync(roleName, projectId, ct);
        var byName = new Dictionary<string, (SkillContent Content, int Rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in rows)
        {
            if (!s.Enabled) continue;
            var rank = (s.ProjectId is not null ? 2 : 0) + (!s.IsGlobal ? 1 : 0);
            if (!byName.TryGetValue(s.Name, out var cur) || rank > cur.Rank)
                byName[s.Name] = (new SkillContent(s.Name, s.Description, s.Body), rank);
        }
        return byName.Values.Select(v => v.Content).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
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

    public Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(AgentType role, string? projectId = null, CancellationToken ct = default)
        => Task.FromResult(_byRole.TryGetValue(role, out var list)
            ? list
            : (IReadOnlyList<SkillContent>)Array.Empty<SkillContent>());
}
