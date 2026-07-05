using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// SkillBootstrap tests: idempotent seeding, role-to-skill mapping,
/// operator-edit preservation. The bootstrap is what wires the
/// operator-maintained godot-ecs-gamedev-playbook into the agent
/// memory layer so every agent prompt sees a per-role skill list
/// and the repo URL.
/// </summary>
public class SkillBootstrapTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MemoryStore _memory;

    public SkillBootstrapTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-skill-{Guid.NewGuid():N}.db");
        _ = new IssueStore(_dbPath);  // bootstrap the v9 schema
        _memory = new MemoryStore(_dbPath);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private SkillBootstrap NewBootstrap() => new(
        _memory, NullLogger<SkillBootstrap>.Instance);

    [Fact]
    public async Task SeedAsync_AllRoles_IncludesAllDefaultKeys()
    {
        await NewBootstrap().SeedAsync();

        var all = await _memory.RecallAsync();
        var keys = all.Select(m => m.Key).ToHashSet();

        Assert.Contains(SkillBootstrap.RepoKey, keys);
        Assert.Contains(SkillBootstrap.SnapshotKey, keys);
        // Each AgentType role has its own key.
        foreach (var role in SkillBootstrap.DefaultRoleSkillMap.Keys)
        {
            Assert.Contains($"playbook/skills/{role.ToString().ToLowerInvariant()}", keys);
        }
    }

    [Fact]
    public async Task SeedAsync_RepoKey_HasDefaultUrl()
    {
        await NewBootstrap().SeedAsync();
        var entry = (await _memory.RecallAsync("playbook/repo")).Single();
        Assert.Equal(SkillBootstrap.DefaultRepoUrl, entry.Body);
    }

    [Fact]
    public async Task SeedAsync_Idempotent_DoesNotOverwriteOperatorEdits()
    {
        // First seed.
        await NewBootstrap().SeedAsync();
        // Operator edits the repo URL to a fork.
        var existing = (await _memory.RecallAsync("playbook/repo")).Single();
        var edited = "https://github.com/someone-else/their-fork";
        // We have to update via Forget + Remember; the existing API
        // does not have an "update" method. Forget is the operator's
        // lever. The SeedIfMissing contract is what we test: a
        // second SeedAsync should NOT touch the existing record.
        await _memory.ForgetAsync("playbook/repo");
        await _memory.RememberAsync("playbook/repo", edited);

        // Second seed (idempotent).
        await NewBootstrap().SeedAsync();

        // The operator's edit survives.
        var after = (await _memory.RecallAsync("playbook/repo")).Single();
        Assert.Equal(edited, after.Body);
    }

    [Fact]
    public async Task SeedAsync_TwiceInARow_DoesNotDuplicateRows()
    {
        await NewBootstrap().SeedAsync();
        await NewBootstrap().SeedAsync();
        var all = await _memory.RecallAsync();
        // Each key appears exactly once.
        var grouped = all.GroupBy(m => m.Key);
        foreach (var g in grouped)
        {
            Assert.Single(g);
        }
    }

    [Fact]
    public void DefaultRoleSkillMap_IncludesAllProductionRoles()
    {
        // The map must cover the four AgentType values that the
        // orchestrator dispatches: CoreDev, ClientDev, QA, Reviewer.
        // Intake is also listed for the intake flow. Designer is
        // handled separately (it's an Orchestrator-only role).
        Assert.Contains(AgentType.CoreDev, SkillBootstrap.DefaultRoleSkillMap.Keys);
        Assert.Contains(AgentType.ClientDev, SkillBootstrap.DefaultRoleSkillMap.Keys);
        Assert.Contains(AgentType.QA, SkillBootstrap.DefaultRoleSkillMap.Keys);
        Assert.Contains(AgentType.Reviewer, SkillBootstrap.DefaultRoleSkillMap.Keys);
    }

    [Fact]
    public void DefaultRoleSkillMap_Keys_ArePipeSeparatedSkillNames()
    {
        // The body format is "name1 | name2 | name3". The Designer
        // prompt parses it and tells the model to fetch each by
        // name from the repo URL.
        foreach (var (role, skills) in SkillBootstrap.DefaultRoleSkillMap)
        {
            Assert.NotEmpty(skills);
            foreach (var s in skills)
            {
                Assert.DoesNotContain("|", s);  // individual skill names don't contain pipes
                Assert.DoesNotContain(" ", s);  // ... or spaces (kebab/snake case)
                Assert.False(string.IsNullOrWhiteSpace(s));
            }
        }
    }

    [Fact]
    public void DesignerExtraSkills_OverlapsWithCoreDev_ForConsistentVisualLanguage()
    {
        // The Designer is downstream of CoreDev (the spec the
        // Designer is reviewing was likely authored by CoreDev).
        // Their skills lists share `engine_agnostic_architecture` and
        // `ecs_component_design` so the visual artifacts the
        // Designer produces match CoreDev's existing patterns.
        var coreDev = new HashSet<string>(SkillBootstrap.DefaultRoleSkillMap[AgentType.CoreDev]);
        var overlap = SkillBootstrap.DesignerExtraSkills
            .Where(s => coreDev.Contains(s))
            .ToList();
        Assert.NotEmpty(overlap);
    }
}