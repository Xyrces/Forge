using PortHorizon.Agents.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class SkillStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly AgentStore _agents;
    private readonly SkillStore _skills;

    public SkillStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-skills-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _agents = new AgentStore(_issues);
        _skills = new SkillStore(_issues);
    }

    public void Dispose()
    {
        _skills.Dispose();
        _agents.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Create_AndList_ReturnsSkill()
    {
        await _skills.CreateAsync(new NewSkill("test-patterns", "always write xunit tests"));
        var list = await _skills.ListAsync(null, globalOnly: false);
        Assert.Single(list);
        Assert.Equal("test-patterns", list[0].Name);
        Assert.Null(list[0].AgentId);
    }

    [Fact]
    public async Task Upsert_UpdatesBodyOnSameName()
    {
        await _skills.CreateAsync(new NewSkill("rule", "v1"));
        var updated = await _skills.CreateAsync(new NewSkill("rule", "v2"));
        Assert.Equal("v2", updated.Body);
        var list = await _skills.ListAsync(null, false);
        Assert.Single(list);
    }

    [Fact]
    public async Task List_GlobalOnly_ExcludesAgentScoped()
    {
        var agent = await _agents.CreateAsync(new NewAgent("qa", "QA"));
        await _skills.CreateAsync(new NewSkill("global", "g-body"));
        await _skills.CreateAsync(new NewSkill("agent-only", "a-body", AgentId: agent.Id));
        var global = await _skills.ListAsync(null, globalOnly: true);
        Assert.Single(global);
        Assert.Equal("global", global[0].Name);
    }

    [Fact]
    public async Task Delete_CascadeRemovesAgentSkills()
    {
        var agent = await _agents.CreateAsync(new NewAgent("qa", "QA"));
        await _skills.CreateAsync(new NewSkill("a", "body", AgentId: agent.Id));
        await _agents.DeleteAsync(agent.Id);
        var list = await _skills.ListAsync(null, false);
        Assert.Empty(list);
    }
}
