using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class SkillStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly AgentStore _agents;
    private readonly SkillStore _skills;

    public SkillStoreTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("skills");
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

    [Fact]
    public async Task Upsert_IsScopedPerProject_SameNameCoexistsAcrossScopes()
    {
        await _skills.CreateAsync(new NewSkill("rule", "GLOBAL"));
        await _skills.CreateAsync(new NewSkill("rule", "PROJ1", ProjectId: "proj1", Source: SkillSources.Repo));
        var all = await _skills.ListAsync(null, false);
        Assert.Equal(2, all.Count(s => s.Name == "rule"));

        // Upsert hits only the matching scope.
        await _skills.CreateAsync(new NewSkill("rule", "GLOBAL v2"));
        await _skills.CreateAsync(new NewSkill("rule", "PROJ1 v2", ProjectId: "proj1", Source: SkillSources.Repo));
        all = await _skills.ListAsync(null, false);
        Assert.Equal(2, all.Count(s => s.Name == "rule"));
        Assert.Equal("GLOBAL v2", all.Single(s => s.Name == "rule" && s.ProjectId is null).Body);
        Assert.Equal("PROJ1 v2", all.Single(s => s.Name == "rule" && s.ProjectId == "proj1").Body);
    }

    [Fact]
    public async Task ListForRun_FiltersByRoleAndProject()
    {
        await _skills.CreateAsync(new NewSkill("shared", "S"));                                                    // global, all roles
        await _skills.CreateAsync(new NewSkill("core-only", "C", Roles: new[] { "coredev" }));                     // global, coredev
        await _skills.CreateAsync(new NewSkill("p1-skill", "P1", ProjectId: "proj1"));                             // proj1, all roles
        await _skills.CreateAsync(new NewSkill("p1-core", "P1C", Roles: new[] { "coredev" }, ProjectId: "proj1")); // proj1, coredev
        await _skills.CreateAsync(new NewSkill("p2-skill", "P2", ProjectId: "proj2"));                             // proj2, all roles

        var proj1Core = await _skills.ListForRunAsync("coredev", "proj1", CancellationToken.None);
        Assert.Equal(new[] { "core-only", "p1-core", "p1-skill", "shared" }, proj1Core.Select(s => s.Name).OrderBy(n => n).ToArray());

        var proj1Qa = await _skills.ListForRunAsync("qa", "proj1", CancellationToken.None);
        Assert.Equal(new[] { "p1-skill", "shared" }, proj1Qa.Select(s => s.Name).OrderBy(n => n).ToArray());

        // A run with no project sees global skills only.
        var noProject = await _skills.ListForRunAsync("coredev", null, CancellationToken.None);
        Assert.Equal(new[] { "core-only", "shared" }, noProject.Select(s => s.Name).OrderBy(n => n).ToArray());

        var proj2Qa = await _skills.ListForRunAsync("qa", "proj2", CancellationToken.None);
        Assert.Equal(new[] { "p2-skill", "shared" }, proj2Qa.Select(s => s.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Update_RepoOwned_Throws()
    {
        var row = await _skills.CreateAsync(new NewSkill("repo-skill", "body", ProjectId: "proj1", Source: SkillSources.Repo));
        var ex = await Assert.ThrowsAsync<RepoOwnedSkillException>(() =>
            _skills.UpdateAsync(row.Id, new Dictionary<string, object?> { ["body"] = "edit" }, CancellationToken.None));
        Assert.Contains("repo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_RepoOwned_Throws()
    {
        var row = await _skills.CreateAsync(new NewSkill("repo-skill", "body", ProjectId: "proj1", Source: SkillSources.Repo));
        await Assert.ThrowsAsync<RepoOwnedSkillException>(() => _skills.DeleteAsync(row.Id, CancellationToken.None));
        // Forge-owned rows delete fine.
        var owned = await _skills.CreateAsync(new NewSkill("ui-skill", "body"));
        await _skills.DeleteAsync(owned.Id, CancellationToken.None);
        Assert.DoesNotContain(await _skills.ListAsync(null, false), s => s.Name == "ui-skill");
    }

    [Fact]
    public async Task DeleteRepoSkillsNotIn_RemovesOnlyStaleRepoRows()
    {
        await _skills.CreateAsync(new NewSkill("keep", "K", ProjectId: "proj1", Source: SkillSources.Repo));
        await _skills.CreateAsync(new NewSkill("stale", "S", ProjectId: "proj1", Source: SkillSources.Repo));
        await _skills.CreateAsync(new NewSkill("ui-row", "U", ProjectId: "proj1"));                                  // forge-owned, same project
        await _skills.CreateAsync(new NewSkill("other-proj", "O", ProjectId: "proj2", Source: SkillSources.Repo));   // repo row, different project

        var deleted = await _skills.DeleteRepoSkillsNotInAsync("proj1", new[] { "keep" }, CancellationToken.None);
        Assert.Equal(1, deleted);

        var all = await _skills.ListAsync(null, false);
        Assert.Contains(all, s => s.Name == "keep");
        Assert.Contains(all, s => s.Name == "ui-row");
        Assert.Contains(all, s => s.Name == "other-proj");
        Assert.DoesNotContain(all, s => s.Name == "stale");
    }
}
