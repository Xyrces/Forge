using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Agents;

public sealed record RoleAgent(
    string KiloAgentName,
    string ProjectSubdir,
    IReadOnlyList<string> AllowedTools);

public sealed class RoleAgentRegistry
{
    private readonly IReadOnlyDictionary<AgentType, RoleAgent> _roles;

    public RoleAgentRegistry()
    {
        _roles = new Dictionary<AgentType, RoleAgent>
        {
            [AgentType.CoreDev]   = new("coredev",   "PortHorizon.Core",   new[] { "bash", "read", "edit", "grep", "glob", "webfetch" }),
            [AgentType.ClientDev] = new("clientdev", "PortHorizon.Client", new[] { "bash", "read", "edit", "grep", "glob", "webfetch" }),
            [AgentType.QA]        = new("qa",        "",                   new[] { "bash", "read", "grep", "glob" }),
            [AgentType.Reviewer]  = new("reviewer",  "",                   new[] { "read", "grep", "glob", "webfetch" }),
        };
    }

    /// <summary>
    /// Designer is an Orchestrator-only role (no AgentType). It's
    /// registered as a custom entry under the key "designer" so
    /// the LLM provider / model can be configured separately in
    /// appsettings.json (llm.roles.designer). Falls back to CoreDev's
    /// kilo agent name when not configured.
    /// </summary>
    public const string DesignerKiloAgentName = "designer";

    /// <summary>
    /// P2.b: Artist is an Orchestrator-only role (no AgentType).
    /// Same pattern as Designer: configured under
    /// llm.roles.artist in appsettings.json; falls back to
    /// CoreDev's kilo agent name when not configured.
    /// </summary>
    public const string ArtistKiloAgentName = "artist";

    /// <summary>
    /// Get a role by its kilo agent name. Returns null when no role
    /// matches. Use this for the Designer (which is keyed by name
    /// not by AgentType).
    /// </summary>
    public RoleAgent? ByKiloAgentName(string kiloAgentName)
    {
        if (string.IsNullOrWhiteSpace(kiloAgentName)) return null;
        foreach (var r in _roles.Values)
        {
            if (string.Equals(r.KiloAgentName, kiloAgentName, StringComparison.Ordinal))
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

    public static AgentType FromTaskType(string taskType) => taskType.ToLowerInvariant() switch
    {
        "ecs" or "systems" or "pathfinding" or "atmospherics" or "mcp" => AgentType.CoreDev,
        "client" or "ui" or "godot" or "syncbridge" => AgentType.ClientDev,
        "test" or "playtest" or "qa" => AgentType.QA,
        "review" => AgentType.Reviewer,
        _ => AgentType.CoreDev
    };
}
