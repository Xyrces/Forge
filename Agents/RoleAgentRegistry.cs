using Forge.Core;

namespace Forge.Agents;

public sealed record RoleAgent(
    string AgentName,
    string ProjectSubdir,
    IReadOnlyList<string> AllowedTools);

public sealed class RoleAgentRegistry
{
    private readonly IReadOnlyDictionary<AgentType, RoleAgent> _roles;

    public RoleAgentRegistry()
    {
        _roles = new Dictionary<AgentType, RoleAgent>
        {
            // ProjectSubdir describes the role's territory inside the
            // *forge* repo (this repository). It feeds the agent's MAF
            // description metadata and the dispatch prompt's boundary
            // rule; the authoritative boundary prose lives in the
            // repo's agents/<role>.md role prompt.
            [AgentType.CoreDev]   = new("coredev",   "Forge backend (Core/, Agents/, Orchestrator/, Dashboard/, Configuration/, Projects/, AgentTools/)", new[] { "bash", "read", "edit", "grep", "glob", "webfetch" }),
            [AgentType.ClientDev] = new("clientdev", "Forge.UI/", new[] { "bash", "read", "edit", "grep", "glob", "webfetch" }),
            [AgentType.QA]        = new("qa",        "",                   new[] { "bash", "read", "grep", "glob" }),
            [AgentType.Reviewer]  = new("reviewer",  "",                   new[] { "read", "grep", "glob", "webfetch" }),
        };
    }

    /// <summary>
    /// Designer is an Orchestrator-only role (no AgentType). It's
    /// registered as a custom entry under the key "designer" so
    /// the LLM provider / model can be configured separately in
    /// appsettings.json (llm.roles.designer). Falls back to CoreDev's
    /// agent name when not configured.
    /// </summary>
    public const string DesignerAgentName = "designer";

    /// <summary>
    /// P2.b: Artist is an Orchestrator-only role (no AgentType).
    /// Same pattern as Designer: configured under
    /// llm.roles.artist in appsettings.json; falls back to
    /// CoreDev's agent name when not configured.
    /// </summary>
    public const string ArtistAgentName = "artist";

    /// <summary>
    /// Get a role by its agent name. Returns null when no role
    /// matches. Use this for the Designer (which is keyed by name
    /// not by AgentType).
    /// </summary>
    public RoleAgent? ByAgentName(string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName)) return null;
        foreach (var r in _roles.Values)
        {
            if (string.Equals(r.AgentName, agentName, StringComparison.Ordinal))
                return r;
        }
        return null;
    }

    public RoleAgent ForType(AgentType type)
        => _roles.TryGetValue(type, out var role)
            ? role
            : throw new InvalidOperationException($"No role agent registered for type {type}");

    public bool TryForType(AgentType type, out RoleAgent? role)
    {
        var ok = _roles.TryGetValue(type, out var r);
        role = r;
        return ok;
    }

    public IEnumerable<AgentType> SupportedTypes => _roles.Keys;

    /// <summary>All registered (AgentType, role) pairs — the Agents
    /// page enumerates this to render one card per role.</summary>
    public IReadOnlyDictionary<AgentType, RoleAgent> All() => _roles;

    /// <summary>Reverse lookup: role descriptor → its AgentType.</summary>
    public AgentType TypeOf(RoleAgent role)
    {
        foreach (var (type, r) in _roles)
            if (ReferenceEquals(r, role) || string.Equals(r.AgentName, role.AgentName, StringComparison.Ordinal))
                return type;
        throw new InvalidOperationException($"Role '{role.AgentName}' is not registered");
    }

    public static AgentType FromTaskType(string taskType) => taskType.ToLowerInvariant() switch
    {
        "ecs" or "systems" or "pathfinding" or "atmospherics" or "mcp" => AgentType.CoreDev,
        "client" or "ui" or "godot" or "syncbridge" => AgentType.ClientDev,
        "test" or "playtest" or "qa" => AgentType.QA,
        "review" => AgentType.Reviewer,
        _ => AgentType.CoreDev
    };
}
