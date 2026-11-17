using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class GroomingRecoveryTests : IDisposable
{
    private readonly IssueStore _issues;
    private readonly IssueGroomerRunStore _runs;
    private readonly SpecStore _specs;

    public GroomingRecoveryTests()
    {
        var path = TempRoot.Instance.NewDbPath("groom-recovery");
        _issues = new IssueStore(path, "p");
        _runs = new IssueGroomerRunStore(path);
        _specs = new SpecStore(_issues);
    }

    public void Dispose() => _issues.Dispose();

    [Fact]
    public async Task InterruptedRunRequiresExplicitExactAttemptRetry_AndSingleClaim()
    {
        var run = await _runs.StartAsync("spec", GroomerTriggerKind.Scheduled, default);
        await _runs.RecoverInterruptedAsync();
        Assert.Equal(GroomerRunStatus.NeedsReview, Assert.Single(await _runs.ListAsync()).Status);
        Assert.Empty(await _runs.ListQueuedAsync());
        Assert.Null(await _runs.TryStartScheduledAsync("spec"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runs.QueueAsync("spec", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runs.QueueAsync("spec", true, run.Id + 1));
        var queued = await _runs.QueueAsync("spec", true, run.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runs.QueueAsync("spec", true, run.Id));
        Assert.Null(await _runs.TryStartScheduledAsync("spec"));
        Assert.True(await _runs.TryClaimQueuedAsync(queued));
        Assert.False(await _runs.TryClaimQueuedAsync(queued));
        await _runs.RecoverInterruptedAsync();
        Assert.Equal(GroomerRunStatus.NeedsReview, (await _runs.ListAsync(limit: 1))[0].Status);
    }

    [Fact]
    public async Task DuplicateAdmissionCreatesOnlyOneQueuedAttempt()
    {
        async Task<bool> Queue()
        {
            try { await _runs.QueueAsync("spec", false); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Queue(), Queue());
        Assert.Single(results, value => value);
        Assert.Single(await _runs.ListQueuedAsync());
    }

    [Fact]
    public async Task DecompositionReadsOnlyExactPendingOwnedTree_RejectsParkedAndForeignSpec()
    {
        var spec = await _specs.CreateAsync(new NewSpec("p", "owned", "body"));
        var story = await _issues.CreateAsync(new NewIssue("story", "story", ParentId: spec.Id));
        var task = await _issues.CreateAsync(new NewIssue("task", "task", ParentId: story.Id));
        await _issues.CreateAsync(new NewIssue("task", "unrelated"));
        var store = new GroomingDecompositionStore(_issues);
        Assert.Equal(2, (await store.ReadPendingAsync(spec.Id)).Count);
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadPendingAsync(spec.Id));
        var foreign = await _specs.CreateAsync(new NewSpec("other", "foreign", "body"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadPendingAsync(foreign.Id));
    }
}
