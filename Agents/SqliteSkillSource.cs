using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// SQLite-backed skill source. Resolves a role to its
/// <c>AgentRecord.Id</c> via <see cref="IAgentStore.GetByKiloNameAsync"/>,
/// then loads global + per-agent skills from <see cref="ISkillStore"/>.
/// </summary>
public sealed class SqliteSkillSource : ISkillSource
{
    private readonly IAgentStore _agents;
    private readonly ISkillStore _skills;
    private readonly RoleAgentRegistry _roles;

    public SqliteSkillSource(IAgentStore agents, ISkillStore skills, RoleAgentRegistry roles)
    {
        _agents = agents;
        _skills = skills;
        _roles = roles;
    }

    public async Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(AgentType role, CancellationToken ct = default)
    {
        var roleDef = _roles.ForType(role);
        var agent = await _agents.GetByKiloNameAsync(roleDef.KiloAgentName, ct);
        var agentId = agent?.Id;

        // Global skills.
        var globals = await _skills.ListAsync(agentId: null, globalOnly: true, ct);

        // Per-agent skills (only if the AgentStore has a row for this role).
        IReadOnlyList<SkillRecord> roleSkills = agentId is null
            ? Array.Empty<SkillRecord>()
            : await _skills.ListAsync(agentId: agentId, globalOnly: false, ct);

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
