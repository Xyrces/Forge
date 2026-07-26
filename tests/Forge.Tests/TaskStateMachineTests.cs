using Forge.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 2 write-path: the transition table's legality rules and the
/// shadow-mode reporting behavior (state metadata recorded, illegal
/// transitions flagged but allowed, never thrown).
/// </summary>
public class TaskStateMachineTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;

    public TaskStateMachineTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private async Task<IssueRecord> SeedTaskAsync(
        IssueStatus status = IssueStatus.Pending, Dictionary<string, object>? meta = null)
    {
        var t = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "t",
            Metadata: meta ?? new Dictionary<string, object>()));
        if (status != IssueStatus.Pending)
        {
            await _issues.TransitionAsync(t.Id, status, error: null);
        }
        return (await _issues.GetAsync(t.Id))!;
    }

    private TaskStateMachine Machine(bool authority = false)
        => new(_issues, authority, NullLogger.Instance);

    [Theory]
    // Happy paths from the 2026-07-25/26 incident trail.
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.Pending, TaskLifecycleState.Dispatching, true)]
    [InlineData(TaskEvent.PrOpened, TaskLifecycleState.Dispatching, TaskLifecycleState.PROpen, true)]
    [InlineData(TaskEvent.CiRedOnPr, TaskLifecycleState.PROpen, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.ParkedOnInfra, TaskLifecycleState.PROpen, TaskLifecycleState.ParkedInfra, true)]
    [InlineData(TaskEvent.BaseRecovered, TaskLifecycleState.ParkedInfra, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.StallDetected, TaskLifecycleState.ReworkRunning, TaskLifecycleState.StalledRework, true)]
    [InlineData(TaskEvent.ReworkFired, TaskLifecycleState.StalledRework, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.ConflictDetected, TaskLifecycleState.MergeReady, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.CiGreen, TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady, true)]
    [InlineData(TaskEvent.Merged, TaskLifecycleState.MergeReady, TaskLifecycleState.Merged, true)]
    [InlineData(TaskEvent.BreakerTripped, TaskLifecycleState.StalledRework, TaskLifecycleState.Failed, true)]
    [InlineData(TaskEvent.OperatorRequeue, TaskLifecycleState.Failed, TaskLifecycleState.Pending, true)]
    // Illegal: these are the corruption shapes the table exists to flag.
    [InlineData(TaskEvent.Merged, TaskLifecycleState.Pending, TaskLifecycleState.Merged, false)]
    [InlineData(TaskEvent.PrOpened, TaskLifecycleState.Merged, TaskLifecycleState.PROpen, false)]
    [InlineData(TaskEvent.ParkedOnInfra, TaskLifecycleState.Merged, TaskLifecycleState.ParkedInfra, false)]
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.PROpen, TaskLifecycleState.Dispatching, false)]
    public void TransitionTable_Legality(TaskEvent evt, TaskLifecycleState from, TaskLifecycleState to, bool expected)
    {
        Assert.Equal(expected, TaskStateMachine.IsLegal(evt, from, to));
    }

    [Fact]
    public void IdempotentReobservation_InTable()
    {
        // Self-transitions are legal ONLY when the table lists them
        // (idempotent re-observation of the same reality).
        Assert.True(TaskStateMachine.IsLegal(TaskEvent.ReworkFired, TaskLifecycleState.ReworkQueued, TaskLifecycleState.ReworkQueued));
        Assert.True(TaskStateMachine.IsLegal(TaskEvent.PrOpened, TaskLifecycleState.PROpen, TaskLifecycleState.PROpen));
        Assert.False(TaskStateMachine.IsLegal(TaskEvent.Merged, TaskLifecycleState.Pending, TaskLifecycleState.Pending));
    }

    [Fact]
    public async Task Report_LegalTransition_RecordsStateMetadata()
    {
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var next = await Machine().ReportAsync(task, TaskEvent.Dispatched, null, false, CancellationToken.None);

        Assert.Equal(TaskLifecycleState.Dispatching, next);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("Dispatching", after.GetMetadata("state"));
        Assert.Equal("Dispatched", after.GetMetadata("lastEvent"));
        Assert.NotNull(after.GetMetadata("stateEnteredAt"));
        Assert.Null(after.GetMetadata("stateViolation"));
    }

    [Fact]
    public async Task Report_IllegalTransition_ShadowMode_AllowsAndFlags()
    {
        // A Pending task reporting Merged (impossible in reality) —
        // shadow mode: allowed, flagged, state unchanged.
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var next = await Machine(authority: false).ReportAsync(task, TaskEvent.Merged, null, false, CancellationToken.None);

        Assert.Equal(TaskLifecycleState.Pending, next);   // no move
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("Pending+Merged", after.GetMetadata("stateViolation"));
    }

    [Fact]
    public async Task Report_IllegalTransition_AuthorityMode_FlagsWithoutThrowing()
    {
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var next = await Machine(authority: true).ReportAsync(task, TaskEvent.Merged, null, false, CancellationToken.None);
        Assert.Equal(TaskLifecycleState.Pending, next);
        Assert.Equal("Pending+Merged", (await _issues.GetAsync(task.Id))!.GetMetadata("stateViolation"));
    }

    [Fact]
    public async Task Report_RealisticSequence_TracksThrough()
    {
        // Dispatch -> PR open -> CI red -> rework -> head moved
        // (push) -> green -> merged: the canonical happy path.
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var m = Machine();

        Assert.Equal(TaskLifecycleState.Dispatching,
            await m.ReportAsync(await Reload(task), TaskEvent.Dispatched, null, false, CancellationToken.None));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, new Dictionary<string, object> { ["prNumber"] = "99" });
        Assert.Equal(TaskLifecycleState.PROpen,
            await m.ReportAsync(await Reload(task), TaskEvent.PrOpened, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.ReworkQueued,
            await m.ReportAsync(await Reload(task), TaskEvent.CiRedOnPr, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.MergeReady,
            await m.ReportAsync(await Reload(task), TaskEvent.CiGreen, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.Merged,
            await m.ReportAsync(await Reload(task), TaskEvent.Merged, null, false, CancellationToken.None));
    }

    private async Task<IssueRecord> Reload(IssueRecord t) => (await _issues.GetAsync(t.Id))!;
}
