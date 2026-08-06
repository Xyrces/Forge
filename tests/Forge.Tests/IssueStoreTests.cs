using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class IssueStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _store;

    public IssueStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-issues-{Guid.NewGuid():N}.db");
        _store = new IssueStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Create_AssignsMonotonicShortIdPerType()
    {
        var a = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        var b = await _store.CreateAsync(new NewIssue(Type: "task", Title: "second"));
        var c = await _store.CreateAsync(new NewIssue(Type: "pr-watch", Title: "watch"));
        Assert.Equal("task-1", a.Id);
        Assert.Equal("task-2", b.Id);
        Assert.Equal("pr-watch-1", c.Id);
    }

    [Fact]
    public async Task Create_StoresMetadata()
    {
        var issue = await _store.CreateAsync(new NewIssue(
            Type: "task",
            Title: "first",
            Description: "details",
            Metadata: new Dictionary<string, object>
            {
                ["branch"] = "agent/x",
                ["complex"] = 42
            }));
        Assert.Equal("agent/x", issue.GetMetadata("branch"));
        Assert.Contains("42", issue.MetadataJson);
    }

    [Fact]
    public async Task Claim_FirstCallerWins()
    {
        var issue = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        var claimed1 = await _store.ClaimAsync(issue.Id, "alice");
        var claimed2 = await _store.ClaimAsync(issue.Id, "bob");
        Assert.NotNull(claimed1);
        Assert.Equal("alice", claimed1!.Assignee);
        Assert.Null(claimed2);
    }

    [Fact]
    public async Task Claim_TransitionsStatusToInProgress()
    {
        var issue = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        await _store.ClaimAsync(issue.Id, "alice");
        var fresh = await _store.GetAsync(issue.Id);
        Assert.NotNull(fresh);
        Assert.Equal(IssueStatus.InProgress, fresh!.Status);
    }

    [Fact]
    public async Task Transition_TerminalStatesSetClosedAt()
    {
        var issue = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        await _store.TransitionAsync(issue.Id, IssueStatus.Completed, null);
        var fresh = await _store.GetAsync(issue.Id);
        Assert.NotNull(fresh!.ClosedAt);
    }

    [Fact]
    public async Task Transition_NonTerminalDoesNotSetClosedAt()
    {
        var issue = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        await _store.TransitionAsync(issue.Id, IssueStatus.InProgress, null);
        var fresh = await _store.GetAsync(issue.Id);
        Assert.Null(fresh!.ClosedAt);
    }

    [Fact]
    public async Task Transition_NonInProgress_ClearsAssignee()
    {
        // assignee means "held by a live run right now" — terminal,
        // Blocked, and requeue transitions all release the hold,
        // otherwise the board shows dead runs as held (operator
        // 2026-08-01: Failed cards displaying assignee=forge).
        var issue = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        await _store.ClaimAsync(issue.Id, "forge");
        Assert.Equal("forge", (await _store.GetAsync(issue.Id))!.Assignee);

        await _store.TransitionAsync(issue.Id, IssueStatus.Failed, "boom");
        Assert.Null((await _store.GetAsync(issue.Id))!.Assignee);

        var blocked = await _store.CreateAsync(new NewIssue(Type: "task", Title: "second"));
        await _store.ClaimAsync(blocked.Id, "forge");
        await _store.TransitionAsync(blocked.Id, IssueStatus.Blocked, "breaker");
        Assert.Null((await _store.GetAsync(blocked.Id))!.Assignee);

        var requeued = await _store.CreateAsync(new NewIssue(Type: "task", Title: "third"));
        await _store.ClaimAsync(requeued.Id, "forge");
        await _store.TransitionAsync(requeued.Id, IssueStatus.Pending, null);
        Assert.Null((await _store.GetAsync(requeued.Id))!.Assignee);
    }

    [Fact]
    public async Task Transition_InProgressMetadataUpdate_PreservesAssignee()
    {
        // Live runs get same-status metadata writes (plan gate,
        // follow-up audit) — those must NOT release the hold.
        var issue = await _store.CreateAsync(new NewIssue(Type: "task", Title: "first"));
        await _store.ClaimAsync(issue.Id, "forge");
        await _store.TransitionAsync(issue.Id, IssueStatus.InProgress, null,
            metadata: new Dictionary<string, object> { ["planGate"] = "approved" });
        var after = await _store.GetAsync(issue.Id);
        Assert.Equal("forge", after!.Assignee);
        Assert.Equal("approved", after.GetMetadata("planGate"));
    }

    [Fact]
    public async Task ReadyAsync_ReturnsOnlyPendingIssues()
    {
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "a"));
        var b = await _store.CreateAsync(new NewIssue(Type: "task", Title: "b"));
        await _store.TransitionAsync(b.Id, IssueStatus.InProgress, null);

        var ready = await _store.ReadyAsync(10);
        Assert.Single(ready);
        Assert.Equal("task-1", ready[0].Id);
    }

    [Fact]
    public async Task ListAsync_FilterByStatus()
    {
        await _store.CreateAsync(new NewIssue(Type: "task", Title: "a"));
        var b = await _store.CreateAsync(new NewIssue(Type: "task", Title: "b"));
        await _store.ClaimAsync(b.Id, "alice");

        var pending = await _store.ListAsync(new IssueFilter { Status = IssueStatus.Pending });
        var inprog = await _store.ListAsync(new IssueFilter { Status = IssueStatus.InProgress });
        Assert.Single(pending);
        Assert.Single(inprog);
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotLoseTasks()
    {
        // Spawn 5 concurrent enqueues. Each task should land in the DB exactly once.
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("N")[..8]).ToList();
        await Task.WhenAll(ids.Select(async id =>
        {
            // Reuse the same IssueStore from outside the lock; SQLite serializes writes.
            await _store.CreateAsync(new NewIssue(Type: "concurrent", Title: id));
        }));
        var all = await _store.ListAsync(new IssueFilter { Type = "concurrent" });
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(i => i.Id).Distinct().Count());
    }
}
