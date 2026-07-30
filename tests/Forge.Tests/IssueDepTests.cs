using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 2 of docs/embedded-issues.md: dependency graph + dispatch gate.
/// </summary>
public class IssueDepTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _store;

    public IssueDepTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("dep");
        _store = new IssueStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private async Task<IssueRecord> CreateTaskAsync(string title = "t")
        => await _store.CreateAsync(new NewIssue(Type: "task", Title: title));

    [Fact]
    public async Task AddDependency_TwoIssues_CreatesEdge()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        var edge = await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        Assert.Equal(a.Id, edge.BlockerId);
        Assert.Equal(b.Id, edge.BlockedId);
        Assert.Equal(IssueDepKind.Blocks, edge.Kind);

        var deps = await _store.DependenciesAsync(b.Id);
        Assert.Single(deps);
        Assert.Equal(a.Id, deps[0].BlockerId);
        Assert.Equal(b.Id, deps[0].BlockedId);
        Assert.Equal(IssueDepKind.Blocks, deps[0].Kind);
    }

    [Fact]
    public async Task AddDependency_SelfLoop_Throws()
    {
        var a = await CreateTaskAsync("A");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AddDependencyAsync(a.Id, a.Id, IssueDepKind.Blocks));
    }

    [Fact]
    public async Task AddDependency_MissingBlocker_Throws()
    {
        var b = await CreateTaskAsync("B");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.AddDependencyAsync("task-doesnotexist", b.Id, IssueDepKind.Blocks));
    }

    [Fact]
    public async Task AddDependency_MissingBlocked_Throws()
    {
        var a = await CreateTaskAsync("A");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.AddDependencyAsync(a.Id, "task-doesnotexist", IssueDepKind.Blocks));
    }

    [Fact]
    public async Task AddDependency_DuplicateKind_IsIdempotent()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        var deps = await _store.DependenciesAsync(b.Id);
        Assert.Single(deps);
    }

    [Fact]
    public async Task AddDependency_SamePair_DifferentKinds_BothEdges()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Related);
        var deps = await _store.DependenciesAsync(b.Id);
        Assert.Equal(2, deps.Count);
        Assert.Contains(deps, e => e.Kind == IssueDepKind.Blocks);
        Assert.Contains(deps, e => e.Kind == IssueDepKind.Related);
    }

    [Fact]
    public async Task RemoveDependency_Existing_ReturnsTrueAndDeletes()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);

        var removed = await _store.RemoveDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        Assert.True(removed);

        var deps = await _store.DependenciesAsync(b.Id);
        Assert.Empty(deps);
    }

    [Fact]
    public async Task RemoveDependency_Missing_ReturnsFalse()
    {
        var a = await CreateTaskAsync("A");
        var removed = await _store.RemoveDependencyAsync(a.Id, "task-nope", IssueDepKind.Blocks);
        Assert.False(removed);
    }

    [Fact]
    public async Task IsBlocked_NoEdges_ReturnsFalse()
    {
        var a = await CreateTaskAsync("A");
        Assert.False(await _store.IsBlockedAsync(a.Id));
    }

    [Fact]
    public async Task IsBlocked_BlockerPending_ReturnsTrue()
    {
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        Assert.True(await _store.IsBlockedAsync(blocked.Id));
        Assert.False(await _store.IsBlockedAsync(blocker.Id));
    }

    [Fact]
    public async Task IsBlocked_BlockerInProgress_ReturnsTrue()
    {
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        await _store.ClaimAsync(blocker.Id, "agent");
        Assert.True(await _store.IsBlockedAsync(blocked.Id));
    }

    [Fact]
    public async Task IsBlocked_BlockerCompleted_ReturnsFalse()
    {
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        await _store.ClaimAsync(blocker.Id, "agent");
        await _store.TransitionAsync(blocker.Id, IssueStatus.Completed, error: null);
        Assert.False(await _store.IsBlockedAsync(blocked.Id));
    }

    [Fact]
    public async Task IsBlocked_BlockerFailed_ReturnsTrue()
    {
        // Failed blockers are intentionally treated as open: the
        // operator must explicitly close them or remove the edge.
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        await _store.ClaimAsync(blocker.Id, "agent");
        await _store.TransitionAsync(blocker.Id, IssueStatus.Failed, error: "boom");
        Assert.True(await _store.IsBlockedAsync(blocked.Id));
    }

    [Fact]
    public async Task IsBlocked_BlockerClosed_ReturnsFalse()
    {
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        await _store.ClaimAsync(blocker.Id, "agent");
        await _store.TransitionAsync(blocker.Id, IssueStatus.Closed, error: null);
        Assert.False(await _store.IsBlockedAsync(blocked.Id));
    }

    [Fact]
    public async Task IsBlocked_OnlyRelatedKind_ReturnsFalse()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Related);
        Assert.False(await _store.IsBlockedAsync(b.Id));
    }

    [Fact]
    public async Task ReadyAsync_BlockedIssue_Excluded()
    {
        var ready = await CreateTaskAsync("ready");
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);

        var list = await _store.ReadyAsync(limit: 100);
        Assert.Contains(list, i => i.Id == ready.Id);
        Assert.Contains(list, i => i.Id == blocker.Id);
        Assert.DoesNotContain(list, i => i.Id == blocked.Id);
    }

    [Fact]
    public async Task ReadyAsync_BlockedThenUnblocked_Reappears()
    {
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);

        Assert.DoesNotContain(await _store.ReadyAsync(100), i => i.Id == blocked.Id);

        await _store.ClaimAsync(blocker.Id, "agent");
        await _store.TransitionAsync(blocker.Id, IssueStatus.Completed, error: null);

        Assert.Contains(await _store.ReadyAsync(100), i => i.Id == blocked.Id);
    }

    [Fact]
    public async Task ClaimAsync_OnBlockedIssue_ReturnsNull()
    {
        var blocker = await CreateTaskAsync("blocker");
        var blocked = await CreateTaskAsync("blocked");
        await _store.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);

        var claim = await _store.ClaimAsync(blocked.Id, "agent");
        Assert.Null(claim);

        // Status didn't change.
        var after = await _store.GetAsync(blocked.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Null(after.Assignee);
    }

    [Fact]
    public async Task DependenciesAsync_ReturnsBothDirections()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        var c = await CreateTaskAsync("C");
        await _store.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        await _store.AddDependencyAsync(c.Id, a.Id, IssueDepKind.Related);

        // From a's perspective: a blocks b, c is related to a.
        var fromA = await _store.DependenciesAsync(a.Id);
        Assert.Equal(2, fromA.Count);
        Assert.Contains(fromA, e => e.BlockerId == a.Id && e.BlockedId == b.Id && e.Kind == IssueDepKind.Blocks);
        Assert.Contains(fromA, e => e.BlockerId == c.Id && e.BlockedId == a.Id && e.Kind == IssueDepKind.Related);
    }
}