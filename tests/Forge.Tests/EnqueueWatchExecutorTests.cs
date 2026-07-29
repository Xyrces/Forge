using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Orchestrator.Workflow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P3 checkpoint 6: EnqueueWatchExecutor creates a pr-watch issue
/// so PRWatcher can monitor the PR's review state.
/// </summary>
public class EnqueueWatchExecutorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;

    public EnqueueWatchExecutorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-watch-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task PrResult_Ok_CreatesNoWatchRow_StateDrivenWatching()
    {
        // State-driven watching (2026-07-29): the stage is a graph
        // placeholder — the task carries prNumber and the sweep
        // discovers it directly. No pr-watch row is ever created.
        var devIssue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "dev"));
        var claimed = new ClaimedIssue(devIssue, ClaimResult.Ok, "/tmp/wt", "agent/dev-task");
        var worktree = new WorktreeReady(claimed, WorktreeResult.Ok, "/tmp/wt", "main");
        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the thing", null);
        var pr = new PrOpened(agent, PrResult.Ok, 4242, "abc1234");

        var result = await EnqueueWatchExecutor.HandleAsync(
            pr, _issues, NullLogger<EnqueueWatchExecutor>.Instance, default);

        Assert.Null(result.WatchIssueId);
        var watches = await _issues.ListAsync(new IssueFilter { Type = AgentTaskTypes.PrWatch });
        Assert.Empty(watches);
    }

    [Fact]
    public async Task PrResult_NoDiff_DoesNotEnqueueWatch()
    {
        var devIssue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "dev"));
        var claimed = new ClaimedIssue(devIssue, ClaimResult.Ok, "/tmp/wt", "agent/dev-task");
        var worktree = new WorktreeReady(claimed, WorktreeResult.Ok, "/tmp/wt", "main");
        var agent = new AgentCompleted(worktree, AgentResult.Ok, "no changes", null);
        var pr = new PrOpened(agent, PrResult.NoDiff, 0, null);

        var watch = await EnqueueWatchExecutor.HandleAsync(
            pr, _issues, NullLogger<EnqueueWatchExecutor>.Instance, default);

        Assert.Null(watch.WatchIssueId);
    }
}