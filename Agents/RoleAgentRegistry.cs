using Forge.Core;

namespace Forge.Agents;

public sealed record RoleAgent(
    string AgentName,
    string ProjectSubdir,
    IReadOnlyList<string> AllowedTools,
    // Structured territory for the deterministic plan-territory
    // gate: repo-relative path prefixes the role may touch, plus
    // whether repo-root files are allowed. ProjectSubdir stays the
    // prose form for prompts.
    IReadOnlyList<string>? TerritoryPrefixes = null,
    bool TerritoryAllowsRootFiles = false);

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
            [AgentType.CoreDev]   = new("coredev",   "Forge backend (Core/, Agents/, Orchestrator/, Dashboard/, Configuration/, Projects/, AgentTools/)", new[] { "bash", "read", "edit", "grep", "glob", "webfetch" },
                TerritoryPrefixes: new[] { "Core/", "Agents/", "Orchestrator/", "Dashboard/", "Configuration/", "Projects/", "AgentTools/", "Reviewer/", "DeploymentPipeline/", "tests/", "tools/", "deploy/", "scripts/", ".github/", "docs/", "agents/", ".kilo/" },
                TerritoryAllowsRootFiles: true),
            [AgentType.ClientDev] = new("clientdev", "Forge.UI/", new[] { "bash", "read", "edit", "grep", "glob", "webfetch" },
                TerritoryPrefixes: new[] { "Forge.UI/", "tests/" }),
            [AgentType.QA]        = new("qa",        "",                   new[] { "bash", "read", "grep", "glob" }),
            [AgentType.Reviewer]  = new("reviewer",  "",                   new[] { "read", "grep", "glob", "webfetch" }),
        };
    }

    /// <summary>
    /// Designer is an Orchestrator-only role (dispatched by the
    /// DesignerScheduler, not per-task). Its model resolves through
    /// its own <see cref="AgentType.Designer"/> (phase 3: no more
    /// CoreDev inheritance).
    /// </summary>
    public const string DesignerAgentName = "designer";

    /// <summary>
    /// P2.b: Artist is an Orchestrator-only role. Same pattern as
    /// Designer: its model resolves through its own
    /// <see cref="AgentType.Artist"/>.
    /// </summary>
    public const string ArtistAgentName = "artist";

    /// <summary>
    /// Resolve the effective plan-gate territory for a role on a given
    /// project: the project's roles_json <c>$territory</c> config is the
    /// ONLY source. When the project configures nothing for the role,
    /// the gate is unconstrained (existence checks still run). The
    /// registry's built-in territories are Forge-repo-shaped prompt
    /// prose — applying them as a GATE fallback rejects every plan
    /// naming a foreign layout (observed live 2026-08-12: talaria's
    /// src/… plans were rejected as "outside coredev's territory
    /// (Core/, Agents/, …)"; porthorizon's PortHorizon.*/ layout is
    /// equally outside). Projects that want hard role separation set
    /// $territory explicitly; the forge project carries its registry
    /// values in roles_json.
    /// </summary>
    public static (IReadOnlyList<string> Prefixes, bool AllowsRootFiles) ResolveTerritory(
        RoleAgent roleDef,
        IReadOnlyDictionary<string, Core.RoleTerritory>? projectTerritories)
    {
        if (projectTerritories is not null
            && projectTerritories.TryGetValue(roleDef.AgentName, out var t))
            return (t.Prefixes, t.AllowsRootFiles);
        return (Array.Empty<string>(), false);
    }

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

    /// <summary>
    /// A pipeline (scheduler-side) role: not dispatched per-task by the
    /// engineering loop, but a first-class agent the operator must be
    /// able to see (operator rule 2026-07-24: nothing hidden).
    /// <see cref="ModelType"/> is the role's own AgentType — every
    /// pipeline role with an LLM resolves its model independently
    /// (phase 3 inheritance cut: designer/groomer/artist used to
    /// borrow CoreDev's client; nothing inherits anymore).
    /// Orchestrator has no LLM at all.
    /// </summary>
    public sealed record PipelineRole(
        string AgentName,
        string Description,
        AgentType? ModelType,
        string Surface);

    /// <summary>
    /// The canonical pipeline-role catalog — the SAME list the project
    /// drill-down's slot grid shows, so both surfaces answer "what
    /// agents exist?" identically.
    /// </summary>
    public static readonly IReadOnlyList<PipelineRole> Pipeline = new[]
    {
        new PipelineRole("artist",       "Visual asset generation (Meshy)",            AgentType.Artist,  "/art"),
        new PipelineRole("designer",     "Spec → design artifacts (hygiene + visuals)", AgentType.Designer, "/designs"),
        new PipelineRole("groomer",      "Spec + ad-hoc technical grooming",           AgentType.Groomer,  "/specs"),
        new PipelineRole("intake",       "Operator intake sessions → epics/specs",     AgentType.Intake,   "/intake"),
        new PipelineRole("orchestrator", "Dispatch loop — no LLM",                     null,                "/flow"),
        new PipelineRole("triage",       "Failure triage — bounded auto-remediation",  AgentType.Triage,   "/triage"),
    };

    /// <summary>
    /// Every role name that gets a SlotTable pool — engineering +
    /// pipeline. Program.BuildSlotTable sizes pools from this so the
    /// drill-down's slot grid and the Agents page show the same set.
    /// </summary>
    public static IReadOnlyList<string> AllSlotRoles
        => new[] { "coredev", "clientdev", "qa", "reviewer" }
            .Concat(Pipeline.Select(p => p.AgentName))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
