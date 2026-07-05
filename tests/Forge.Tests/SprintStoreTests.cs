using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class SprintStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;

    public SprintStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-sprint-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _sprints = new SprintStore(_issues);
    }

    public void Dispose()
    {
        _sprints.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task SingleActive_Invariant_EnforcedOnCreate()
    {
        var now = DateTime.UtcNow;
        await _sprints.CreateAsync(new NewSprint("S1", "g1", now, now.AddDays(7), SprintStatus.Active));
        var second = await _sprints.CreateAsync(new NewSprint("S2", "g2", now, now.AddDays(7), SprintStatus.Active));
        var active = await _sprints.ListAsync(activeOnly: true);
        Assert.Single(active);
        Assert.Equal(second.Id, active[0].Id);
    }

    [Fact]
    public async Task SetActive_ArchivesPrevious()
    {
        var now = DateTime.UtcNow;
        var s1 = await _sprints.CreateAsync(new NewSprint("S1", "g1", now, now.AddDays(7), SprintStatus.Active));
        var s2 = await _sprints.CreateAsync(new NewSprint("S2", "g2", now, now.AddDays(7), SprintStatus.Completed));
        await _sprints.SetActiveAsync(s2.Id);
        var active = await _sprints.GetActiveAsync();
        Assert.Equal(s2.Id, active!.Id);
        var s1After = await _sprints.GetAsync(s1.Id);
        Assert.Equal(SprintStatus.Archived, s1After!.Status);
    }

    [Fact]
    public async Task AddIssue_AndList_Work()
    {
        var now = DateTime.UtcNow;
        var sprint = await _sprints.CreateAsync(new NewSprint("S", "g", now, now.AddDays(7)));
        var issue = await _issues.CreateAsync(new NewIssue("task", "T1"));
        await _sprints.AddIssueAsync(sprint.Id, issue.Id);
        var ids = await _sprints.GetIssueIdsAsync(sprint.Id);
        Assert.Equal(new[] { issue.Id }, ids);
    }

    [Fact]
    public async Task ReadyAsync_FiltersBySprintMembership()
    {
        var now = DateTime.UtcNow;
        var sprint = await _sprints.CreateAsync(new NewSprint("S", "g", now, now.AddDays(7), SprintStatus.Active));
        var inSprint = await _issues.CreateAsync(new NewIssue("task", "T1"));
        var notInSprint = await _issues.CreateAsync(new NewIssue("task", "T2"));
        await _sprints.AddIssueAsync(sprint.Id, inSprint.Id);
        var ready = await _issues.ReadyAsync(10, sprint.Id);
        Assert.Single(ready);
        Assert.Equal(inSprint.Id, ready[0].Id);

        var allReady = await _issues.ReadyAsync(10, sprintId: null);
        Assert.Equal(2, allReady.Count);
    }
}
