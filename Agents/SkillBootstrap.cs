using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Bootstraps the Xyrces/godot-ecs-gamedev-playbook reference into
/// the operator-editable memory layer. Each role gets a per-role
/// memory key under <c>playbook/skills/&lt;role&gt;</c> that lists
/// the skill names that role should know about. Agent prompts
/// read the relevant key and the model can decide which skills to
/// fetch from the repo URL.
///
/// <para>
/// The bootstrap is idempotent. <see cref="MemoryStore.SeedIfMissingAsync"/>
/// skips writes when the key already exists, so operator edits
/// survive orchestrator restarts.
/// </para>
///
/// <para>
/// To update the playbook (e.g. the operator pushes new skills to
/// the upstream repo), the operator deletes the relevant memory
/// key (or just the per-role one) and the next orchestrator start
/// re-seeds the default. The body of the per-role key is a
/// pipe-separated list of skill names — the agent's system
/// prompt tells the model to fetch the relevant SKILL.md from
/// the upstream repo by name.
/// </para>
/// </summary>
public sealed class SkillBootstrap
{
    private readonly MemoryStore _memory;
    private readonly ILogger<SkillBootstrap> _logger;

    public SkillBootstrap(MemoryStore memory, ILogger<SkillBootstrap> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    public const string RepoKey = "playbook/repo";
    public const string SnapshotKey = "playbook/snapshot";

    /// <summary>
    /// Default repo URL. Operator can override by deleting the
    /// memory key (or editing it directly in memory.db).
    /// </summary>
    public const string DefaultRepoUrl =
        "https://github.com/Xyrces/godot-ecs-gamedev-playbook";

    public const string DefaultSnapshot =
        "2026-07-03: 39 skills across 8 categories (Architecture/ECS, " +
        "GameLoop/Physics/Spatial, Visual2D, Visual3D, UI/UX, " +
        "Audio/State/Networking, Performance/Testing/Infra). " +
        "Skills live under /skills/&lt;name&gt;/SKILL.md in the repo.";

    /// <summary>
    /// Run the bootstrap. Idempotent: re-runs are no-ops if the
    /// operator hasn't cleared the keys.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await _memory.SeedIfMissingAsync(RepoKey, DefaultRepoUrl, ttlDays: null, ct);
        await _memory.SeedIfMissingAsync(SnapshotKey, DefaultSnapshot, ttlDays: null, ct);

        foreach (var (role, skills) in DefaultRoleSkillMap)
        {
            var key = $"playbook/skills/{role.ToString().ToLowerInvariant()}";
            var body = string.Join(" | ", skills);
            await _memory.SeedIfMissingAsync(key, body, ttlDays: null, ct);
        }

        _logger.LogInformation(
            "SkillBootstrap: seeded {N} memory keys under playbook/* (idempotent; operator edits preserved).",
            2 + DefaultRoleSkillMap.Count);
    }

    /// <summary>
    /// Per-role skill list. Each role gets the skills that are
    /// relevant to its work. The list is intentionally short —
    /// the model decides which to actually fetch, so the memory
    /// key is small and the prompt isn't bloated.
    /// </summary>
    public static readonly IReadOnlyDictionary<Core.AgentType, IReadOnlyList<string>> DefaultRoleSkillMap
        = new Dictionary<Core.AgentType, IReadOnlyList<string>>
        {
            [Core.AgentType.CoreDev] = new[]
            {
                "ecs_component_design",
                "ecs_system_design",
                "ecs_world_management",
                "ecs_query_patterns",
                "fixed_timestep_game_loop",
                "game_testing_bdd",
            },
            [Core.AgentType.ClientDev] = new[]
            {
                "project_structure_dotnet_godot",
                "game_ui_architecture",
                "game_ui_visual_design",
                "game_ui_ux_patterns",
                "3d_rendering_pipeline",
                "sound_design_integration",
            },
            [Core.AgentType.QA] = new[]
            {
                "game_testing_bdd",
                "fixed_timestep_game_loop",
                "physics_engine_integration",
                "ecs_query_patterns",
                "scene_level_management",
            },
            [Core.AgentType.Reviewer] = new[]
            {
                "engine_agnostic_architecture",
                "project_structure_dotnet_godot",
                "ecs_component_design",
                "game_testing_bdd",
            },
            // Designer-specific role: lives in Orchestrator namespace
            // but is read by DesignerAgent as the "Designer role".
            // Listed here so the bootstrap is symmetric.
            [Core.AgentType.Intake] = new[]
            {
                "engine_agnostic_architecture",
                "project_structure_dotnet_godot",
                "game_ui_ux_patterns",
            },
        };

    /// <summary>
    /// Skills the Designer agent reads in addition to its
    /// role-specific list. Designer is an Orchestrator-only role
    /// (no AgentType), so we expose this as a separate constant.
    /// </summary>
    public static readonly IReadOnlyList<string> DesignerExtraSkills = new[]
    {
        "engine_agnostic_architecture",
        "ecs_component_design",
        "game_ui_visual_design",
        "game_ui_ux_patterns",
        "game_ui_architecture",
    };
}