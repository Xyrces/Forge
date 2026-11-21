using Forge.Core;
using Forge.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class PublishedRunLifecycleTests
{
    [Theory]
    [InlineData(DispatchCheckpoint.PrOpened, TaskLifecycleState.AgentRunning)]
    [InlineData(DispatchCheckpoint.PushDone, TaskLifecycleState.ReworkRunning)]
    public async Task PublishedCheckpoint_ReconcilesMissingRunCompletion_ThenAllowsReview(DispatchCheckpoint checkpoint, TaskLifecycleState state)
    {
        await using var issues = new IssueStore(Path.Combine(TempRoot.Instance.NewDirectory("published-run"), "state.db"));
        var issue = await issues.CreateAsync(new NewIssue("dev", "published", Metadata: new Dictionary<string, object>
        { ["state"] = state.ToString(), ["prNumber"] = 124, ["stateViolation"] = "AgentRunning+PrOpened" }));
        await issues.TransitionAsync(issue.Id, IssueStatus.InProgress, null);
        await issues.SetCheckpointAsync(issue.Id, checkpoint);
        var machine = new TaskStateMachine(true, NullLogger.Instance);

        var reconciled = await PublishedRunLifecycle.ReconcileAsync(issues, machine, issue, default);
        Assert.Equal("PROpen", reconciled.GetMetadata("state"));
        Assert.Equal("RunCompletedDiff", reconciled.GetMetadata("lastEvent"));
        Assert.Equal("AgentRunning+PrOpened", reconciled.GetMetadata("stateViolation")); // historical evidence retained
        await machine.ReportAsync(issues, reconciled, TaskEvent.ReviewApproved, null, false, default);
        var approved = (await issues.GetAsync(issue.Id))!;
        Assert.Equal("ReviewApproved", approved.GetMetadata("lastEvent"));
        Assert.Equal("AgentRunning+PrOpened", approved.GetMetadata("stateViolation")); // no new illegal transition
    }

    [Theory]
    [InlineData(DispatchCheckpoint.PrOpened, TaskLifecycleState.ReworkQueued, IssueStatus.InProgress, true)]
    [InlineData(DispatchCheckpoint.WorktreeAcquired, TaskLifecycleState.AgentRunning, IssueStatus.InProgress, true)]
    [InlineData(DispatchCheckpoint.AgentCompleted, TaskLifecycleState.AgentRunning, IssueStatus.InProgress, true)]
    [InlineData(DispatchCheckpoint.PushDone, TaskLifecycleState.AgentRunning, IssueStatus.InProgress, false)]
    [InlineData(DispatchCheckpoint.PrOpened, TaskLifecycleState.MergeReady, IssueStatus.InProgress, true)]
    [InlineData(DispatchCheckpoint.PrOpened, TaskLifecycleState.Merged, IssueStatus.Completed, true)]
    public async Task PartialUnpublishedApprovedAndTerminalTasks_DoNotAdvance(DispatchCheckpoint checkpoint,
        TaskLifecycleState state, IssueStatus status, bool hasPr)
    {
        await using var issues = new IssueStore(Path.Combine(TempRoot.Instance.NewDirectory("not-published"), "state.db"));
        var metadata = new Dictionary<string, object> { ["state"] = state.ToString() };
        if (hasPr) metadata["prNumber"] = 124;
        var issue = await issues.CreateAsync(new NewIssue("dev", "work", Metadata: metadata));
        await issues.TransitionAsync(issue.Id, status, null);
        await issues.SetCheckpointAsync(issue.Id, checkpoint);
        var reconciled = await PublishedRunLifecycle.ReconcileAsync(issues,
            new TaskStateMachine(true, NullLogger.Instance), issue, default);
        Assert.Equal(state.ToString(), reconciled.GetMetadata("state"));
        Assert.Null(reconciled.GetMetadata("lastEvent"));
    }
    [Fact]
    public async Task OverlappingReconciliations_RereadAfterFirstAndCannotUndoApproval()
    {
        await using var inner = new IssueStore(Path.Combine(TempRoot.Instance.NewDirectory("published-race"), "state.db"));
        var issue = await inner.CreateAsync(new NewIssue("dev", "published", Metadata: new Dictionary<string, object>
        { ["state"] = "AgentRunning", ["prNumber"] = 124 }));
        await inner.TransitionAsync(issue.Id, IssueStatus.InProgress, null);
        await inner.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened);
        var store = new PausingStore(inner);
        var machine = new TaskStateMachine(true, NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = PublishedRunLifecycle.ReconcileAsync(store, machine, issue, timeout.Token);
        await store.FirstRead.Task.WaitAsync(timeout.Token);
        var second = PublishedRunLifecycle.ReconcileAsync(store, machine, issue, timeout.Token);
        try
        {
            Assert.Equal(1, store.Reads); // second must not capture the old AgentRunning snapshot
            store.AllowRead.TrySetResult();
            await store.Published.Task.WaitAsync(timeout.Token);
            var published = (await inner.GetAsync(issue.Id))!;
            await machine.ReportAsync(inner, published, TaskEvent.ReviewApproved, null, false, timeout.Token);
        }
        finally
        {
            store.AllowRead.TrySetResult();
            store.AllowPublishReturn.TrySetResult();
        }
        await Task.WhenAll(first, second);
        var approved = (await inner.GetAsync(issue.Id))!;
        Assert.Equal("MergeReady", approved.GetMetadata("state"));
        Assert.Equal("ReviewApproved", approved.GetMetadata("lastEvent"));
        Assert.Null(approved.GetMetadata("stateViolation"));
    }

    private sealed class PausingStore(IIssueStore inner) : IIssueStore
    {
        public int Reads;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowPublishReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IssueRecord?> GetAsync(string id, CancellationToken ct = default)
        {
            var readNumber = Interlocked.Increment(ref Reads);
            var snapshot = await inner.GetAsync(id, ct);
            if (readNumber == 1)
            {
                FirstRead.TrySetResult();
                await AllowRead.Task.WaitAsync(ct);
            }
            return snapshot;
        }
        public async Task<IssueRecord> TransitionAsync(string id, IssueStatus to, string? error,
            IDictionary<string, object>? metadata = null, CancellationToken ct = default)
        {
            var result = await inner.TransitionAsync(id, to, error, metadata, ct);
            Published.TrySetResult();
            await AllowPublishReturn.Task.WaitAsync(ct);
            return result;
        }
        public Task<IssueRecord> CreateAsync(NewIssue spec, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueRecord>> ListAsync(IssueFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, string? sprintId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IssueRecord?> ClaimAsync(string id, string assignee, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReparentAsync(string id, string newParentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSummaryAsync(string id, string title, string? description, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IssueEventRecord> AddEventAsync(string id, string kind, string? detail = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetCheckpointAsync(string id, DispatchCheckpoint checkpoint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueRecord>> ListInProgressForRecoveryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> IncrementRecoveryAttemptsAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueEventRecord>> ListEventsAsync(string issueId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IssueEdge> AddDependencyAsync(string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveDependencyAsync(string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueEdge>> DependenciesAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsBlockedAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HashSet<string>> ListBlockersOfAsync(IReadOnlyCollection<string> blockedIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> OpenBlockersAsync(IReadOnlyCollection<string> blockedIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HashSet<string>> OpenBlockingAsync(IReadOnlyCollection<string> blockerIds, CancellationToken ct = default) => throw new NotSupportedException();
    }

}

