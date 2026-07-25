using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class InMemorySkillSourceTests
{
    [Fact]
    public async Task LoadForRole_EmptySource_ReturnsEmpty()
    {
        var src = new InMemorySkillSource(new Dictionary<AgentType, IReadOnlyList<SkillContent>>());
        var skills = await src.LoadForRoleAsync(AgentType.CoreDev);
        Assert.Empty(skills);
    }

    [Fact]
    public async Task LoadForRole_ReturnsConfiguredSkills()
    {
        var src = new InMemorySkillSource(new Dictionary<AgentType, IReadOnlyList<SkillContent>>
        {
            [AgentType.CoreDev] = new[]
            {
                new SkillContent("ecs-style", "How we write ECS code", "Use components over inheritance."),
            },
            [AgentType.QA] = new[]
            {
                new SkillContent("playtest-checklist", null, "Run all four scenarios before approving."),
            },
        });
        var coreDev = await src.LoadForRoleAsync(AgentType.CoreDev);
        var qa = await src.LoadForRoleAsync(AgentType.QA);
        var reviewer = await src.LoadForRoleAsync(AgentType.Reviewer);

        Assert.Single(coreDev);
        Assert.Equal("ecs-style", coreDev[0].Name);
        Assert.Single(qa);
        Assert.Equal("playtest-checklist", qa[0].Name);
        Assert.Empty(reviewer); // not configured -> empty
    }
}

public class SqliteSkillSourceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SkillStore _skills;
    private readonly RoleAgentRegistry _roles;
    private readonly SqliteSkillSource _source;

    public SqliteSkillSourceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-skills-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _skills = new SkillStore(_issues);
        _roles = new RoleAgentRegistry();
        _source = new SqliteSkillSource(_skills, _roles);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task LoadForRole_NoSkills_ReturnsEmpty()
    {
        var skills = await _source.LoadForRoleAsync(AgentType.CoreDev);
        Assert.Empty(skills);
    }

    [Fact]
    public async Task LoadForRole_OnlyGlobals_ReturnsAllGlobals()
    {
        await _skills.CreateAsync(new NewSkill(Name: "global-a", Body: "A body"));
        await _skills.CreateAsync(new NewSkill(Name: "global-b", Body: "B body"));
        var skills = await _source.LoadForRoleAsync(AgentType.CoreDev);
        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "global-a");
        Assert.Contains(skills, s => s.Name == "global-b");
    }

    [Fact]
    public async Task LoadForRole_RoleScopedSkills_AreIsolatedToThatRole()
    {
        // Role-NAME scoping (schema v22): coredev skill only shows up
        // for CoreDev; reviewer skill only for Reviewer — no agent
        // table involved.
        await _skills.CreateAsync(new NewSkill(Name: "ecs-style", Body: "X", Role: "coredev"));
        await _skills.CreateAsync(new NewSkill(Name: "tone-of-voice", Body: "Y", Role: "reviewer"));
        await _skills.CreateAsync(new NewSkill(Name: "global-rule", Body: "Z"));

        var coreSkills = await _source.LoadForRoleAsync(AgentType.CoreDev);
        var reviewerSkills = await _source.LoadForRoleAsync(AgentType.Reviewer);
        var qaSkills = await _source.LoadForRoleAsync(AgentType.QA);

        Assert.Equal(2, coreSkills.Count); // ecs-style + global-rule
        Assert.Contains(coreSkills, s => s.Name == "ecs-style");
        Assert.Contains(coreSkills, s => s.Name == "global-rule");

        Assert.Equal(2, reviewerSkills.Count); // tone-of-voice + global-rule
        Assert.Contains(reviewerSkills, s => s.Name == "tone-of-voice");
        Assert.DoesNotContain(reviewerSkills, s => s.Name == "ecs-style");

        Assert.Single(qaSkills); // global-rule only
        Assert.Equal("global-rule", qaSkills[0].Name);
    }

    [Fact]
    public async Task LoadForRole_DisabledSkill_IsExcluded()
    {
        await _skills.CreateAsync(new NewSkill(Name: "on", Body: "A", Enabled: true));
        await _skills.CreateAsync(new NewSkill(Name: "off", Body: "B", Enabled: false));
        var skills = await _source.LoadForRoleAsync(AgentType.CoreDev);
        Assert.Single(skills);
        Assert.Equal("on", skills[0].Name);
    }

    [Fact]
    public async Task LoadForRole_SkillNameCollision_GlobalThenRole_RoleWins()
    {
        await _skills.CreateAsync(new NewSkill(Name: "build-style", Body: "GLOBAL VERSION"));
        await _skills.CreateAsync(new NewSkill(Name: "build-style", Body: "ROLE VERSION", Role: "coredev"));

        var skills = await _source.LoadForRoleAsync(AgentType.CoreDev);
        Assert.Single(skills);
        Assert.Equal("build-style", skills[0].Name);
        // Role-specific copy wins.
        Assert.Equal("ROLE VERSION", skills[0].Body);
    }
}
