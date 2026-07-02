using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Orchestrator.Workflow;
using Xunit;

namespace PortHorizon.Agents.Tests;

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
    public async Task PrResult_Ok_EnqueuesWatchIssueWithPrMetadata()
    {
        var devIssue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "dev"));
        var claimed = new ClaimedIssue(devIssue, ClaimResult.Ok, "/tmp/wt", "agent/dev-task");
        var worktree = new WorktreeReady(claimed, WorktreeResult.Ok, "/tmp/wt", "main");
        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the thing", null);
        var pr = new PrOpened(agent, PrResult.Ok, 4242, "abc1234");

        var watch = await EnqueueWatchExecutor.HandleAsync(
            pr, _issues, NullLogger<EnqueueWatchExecutor>.Instance, default);

        Assert.NotNull(watch.WatchIssueId);
        var watchIssue = await _issues.GetAsync(watch.WatchIssueId!);
        Assert.NotNull(watchIssue);
        Assert.Equal(AgentTaskTypes.PrWatch, watchIssue!.Type);
        Assert.Equal("4242", watchIssue.GetMetadata("prNumber"));
        Assert.Equal("agent/dev-task", watchIssue.GetMetadata("branch"));
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