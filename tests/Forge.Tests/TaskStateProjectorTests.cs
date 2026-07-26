using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 1 read-model validation: replay the incident scenarios from
/// the 2026-07-25/26 bug trail and assert the projector derives the
/// state the operator needed to see at that moment.
/// </summary>
public class TaskStateProjectorTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    private static IssueRecord Task(
        IssueStatus status,
        Dictionary<string, object>? meta = null,
        DateTime? updatedAt = null) => new(
        Id: "task-1", ShortId: "task-1", Type: "task", Title: "t",
        Description: null, Status: status, Priority: 2, Assignee: null,
        CreatedAt: Now.AddHours(-2), UpdatedAt: updatedAt ?? Now.AddMinutes(-5),
        ClosedAt: null,
        MetadataJson: meta is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(meta));

    private static IssueRecord Watch(Dictionary<string, object> meta) => new(
        Id: "pr-watch-1", ShortId: "pr-watch-1", Type: AgentTaskTypes.PrWatch, Title: "w",
        Description: null, Status: IssueStatus.Pending, Priority: 2, Assignee: null,
        CreatedAt: Now.AddHours(-1), UpdatedAt: Now.AddMinutes(-1),
        ClosedAt: null,
        MetadataJson: System.Text.Json.JsonSerializer.Serialize(meta));

    [Fact]
    public void FreshTask_Pending()
    {
        var info = TaskStateProjector.Derive(Task(IssueStatus.Pending), null, false, Now);
        Assert.Equal(TaskLifecycleState.Pending, info.State);
    }

    [Fact]
    public void Dispatched_NoRunYet_Dispatching()
    {
        var info = TaskStateProjector.Derive(Task(IssueStatus.InProgress), null, false, Now);
        Assert.Equal(TaskLifecycleState.Dispatching, info.State);
    }

    [Fact]
    public void LiveRun_AgentRunning_StartingSubstate()
    {
        var info = TaskStateProjector.Derive(Task(IssueStatus.InProgress), null, true, Now);
        Assert.Equal(TaskLifecycleState.AgentRunning, info.State);
        Assert.Equal("starting", info.Substate);
    }

    [Fact]
    public void LiveRun_PlanGatePending_PlanningSubstate()
    {
        // The plan gate's first live scenario (task-165, 2026-07-26):
        // run active, plan submitted but not yet approved.
        var task = Task(IssueStatus.InProgress, new() { ["planGate"] = """{"approved":false,"revisions":1,"failed":false}""" });
        var info = TaskStateProjector.Derive(task, null, true, Now);
        Assert.Equal(TaskLifecycleState.AgentRunning, info.State);
        Assert.Equal("planning", info.Substate);
    }

    [Fact]
    public void LiveRun_PlanApproved_ImplementingSubstate()
    {
        var task = Task(IssueStatus.InProgress, new() { ["planGate"] = """{"approved":true,"revisions":2,"failed":false}""" });
        var info = TaskStateProjector.Derive(task, null, true, Now);
        Assert.Equal("implementing", info.Substate);
    }

    [Fact]
    public void ReworkFired_TaskPending_ReworkQueued()
    {
        // Watch consumed round 1, task back to Pending waiting for a
        // slot (every CI-failure rework round this week).
        var task = Task(IssueStatus.Pending, new() { ["prNumber"] = "34", ["reworkAttempts"] = "1", ["reworkReason"] = "CI failed for c138594" });
        var watch = Watch(new() { ["reworkInFlightSha"] = "c138594" });
        var info = TaskStateProjector.Derive(task, watch, false, Now);
        Assert.Equal(TaskLifecycleState.ReworkQueued, info.State);
        Assert.Equal(1, info.Strikes);
    }

    [Fact]
    public void GuidedRequeue_PendingWithPrAndReason_ReworkQueued()
    {
        // The operator's guided requeue (2026-07-26): requeue endpoint
        // set reworkReason/reworkContext without a watch marker.
        var task = Task(IssueStatus.Pending, new() { ["prNumber"] = "35", ["reworkReason"] = "infra CI outage recovered — restore branch and retrigger CI" });
        var info = TaskStateProjector.Derive(task, null, false, Now);
        Assert.Equal(TaskLifecycleState.ReworkQueued, info.State);
    }

    [Fact]
    public void NoOpRound_MarkerEqualsHead_StaleTask_StalledRework()
    {
        // task-161 (2026-07-25): the no-op rework round — marker set,
        // no push, task InProgress and untouched for hours. The
        // sprint deadlocked until the stall-breaker shipped.
        var task = Task(IssueStatus.InProgress,
            new() { ["prNumber"] = "34", ["reworkAttempts"] = "1" },
            updatedAt: Now.AddHours(-3));
        var watch = Watch(new() { ["reworkInFlightSha"] = "c138594" });
        var info = TaskStateProjector.Derive(task, watch, false, Now);
        Assert.Equal(TaskLifecycleState.StalledRework, info.State);
        Assert.Contains("stalled", info.WaitingOn);
    }

    [Fact]
    public void ClaimedRound_FreshUpdate_ReworkRunning()
    {
        var task = Task(IssueStatus.InProgress, new() { ["prNumber"] = "34", ["reworkAttempts"] = "1" },
            updatedAt: Now.AddMinutes(-3));
        var watch = Watch(new() { ["reworkInFlightSha"] = "c138594" });
        var info = TaskStateProjector.Derive(task, watch, false, Now);
        Assert.Equal(TaskLifecycleState.ReworkRunning, info.State);
    }

    [Fact]
    public void InfraRedMain_ParkedInfra()
    {
        // The harness-red-main scenario: watch parked, zero strikes
        // burning while the base is broken.
        var task = Task(IssueStatus.InProgress, new() { ["prNumber"] = "40" });
        var watch = Watch(new() { ["parkedOnMainCiSha"] = "0b5ab06" });
        var info = TaskStateProjector.Derive(task, watch, false, Now);
        Assert.Equal(TaskLifecycleState.ParkedInfra, info.State);
        Assert.Contains("base-branch CI recovery", info.WaitingOn);
    }

    [Fact]
    public void PrOpenNoVerdict_PROpen()
    {
        var task = Task(IssueStatus.InProgress, new() { ["prNumber"] = "34" });
        var info = TaskStateProjector.Derive(task, Watch(new()), false, Now);
        Assert.Equal(TaskLifecycleState.PROpen, info.State);
    }

    [Fact]
    public void ApprovedAtHead_MergeReady()
    {
        var task = Task(IssueStatus.InProgress, new() { ["prNumber"] = "34" });
        var watch = Watch(new() { ["reviewVerdict"] = "Approve", ["reviewSha"] = "d584303f" });
        var info = TaskStateProjector.Derive(task, watch, false, Now);
        Assert.Equal(TaskLifecycleState.MergeReady, info.State);
    }

    [Fact]
    public void Merged_Completed_WithPr_Merged()
    {
        var task = Task(IssueStatus.Completed, new() { ["prNumber"] = "41" });
        var info = TaskStateProjector.Derive(task, null, false, Now);
        Assert.Equal(TaskLifecycleState.Merged, info.State);
    }

    [Fact]
    public void BreakerTripped_Failed()
    {
        var task = Task(IssueStatus.Failed, new() { ["prNumber"] = "35", ["reworkAttempts"] = "3" });
        var info = TaskStateProjector.Derive(task, null, false, Now);
        Assert.Equal(TaskLifecycleState.Failed, info.State);
        Assert.Equal(3, info.Strikes);
    }

    [Fact]
    public void BlockedForOperator_BlockedOperator()
    {
        var info = TaskStateProjector.Derive(Task(IssueStatus.Blocked), null, false, Now);
        Assert.Equal(TaskLifecycleState.BlockedOperator, info.State);
    }
}
