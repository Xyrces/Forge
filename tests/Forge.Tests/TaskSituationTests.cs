using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class TaskSituationTests
{
    private static IssueRecord Task(IssueStatus status, params (string Key, string Value)[] meta) =>
        new(Id: "task-1", ShortId: "1", Type: "task", Title: "t", Description: null,
            Status: status, Priority: 2, Assignee: null, CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow, ClosedAt: null,
            MetadataJson: meta.Length == 0
                ? "{}"
                : "{" + string.Join(",", meta.Select(m => $"\"{m.Key}\":\"{m.Value}\"")) + "}",
            ParentIssueId: null, DispatchCheckpoint: null, CheckpointAt: null, RecoveryAttempts: 0);

    [Fact]
    public void FailedVerify_IsActionable_WithNextStep()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Failed, ("lastError", "pre-push verification failed: `dotnet test` exited 1")));
        Assert.Equal("action", s.Tone);
        Assert.Contains("build/tests failed", s.Text);
        Assert.Contains("requeue", s.Text);
    }

    [Fact]
    public void FailedHygiene_HasOwnClass()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Failed, ("lastError", "pre-push hygiene check failed (junk/oversized files added)")));
        Assert.Contains("junk/oversized", s.Text);
    }

    [Fact]
    public void FailedRateLimit_MarkedTransient()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Failed, ("lastError", "llm-429: rate limit reached")));
        Assert.Equal("action", s.Tone);
        Assert.Contains("transient", s.Text);
    }

    [Fact]
    public void BlockedReviewerUnavailable_IsWarnNotAction()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Blocked,
            ("blockedKind", "reviewer-unavailable"), ("lastError", "reviewer unavailable")));
        Assert.Equal("warn", s.Tone);
        Assert.Contains("auto-resumes", s.Text);
    }

    [Fact]
    public void BlockedCircuitBreaker_NamesReasonAndRemedy()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Blocked,
            ("lastError", "PR conflicts with base branch (circuit breaker tripped after max rework attempts)"),
            ("reworkReason", "PR conflicts with the base branch")));
        Assert.Equal("action", s.Tone);
        Assert.Contains("conflict syncs", s.Text);
        Assert.Contains("clear strikes", s.Text);
    }

    [Fact]
    public void PendingReworkRound_ShowsRoundAndReason()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Pending,
            ("prNumber", "742"), ("reworkAttempts", "2"), ("reworkReason", "CI failed for abc: Failure")));
        Assert.Equal("info", s.Tone);
        Assert.Contains("R2/3", s.Text);
        Assert.Contains("CI red", s.Text);
    }

    [Fact]
    public void InProgressMergeReady_ShowsMerging()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "MergeReady")));
        Assert.Contains("merging", s.Text);
    }

    [Fact]
    public void InProgressPROpenApproved_ShowsMergeGateWait()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "PROpen"), ("reviewVerdict", "Approve")));
        Assert.Contains("approved", s.Text);
        Assert.Contains("merge gate", s.Text);
    }

    [Fact]
    public void StalledRework_ExplainsWatcherRefire()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "StalledRework")));
        Assert.Equal("warn", s.Tone);
        Assert.Contains("re-fires", s.Text);
    }

    [Fact]
    public void Completed_HasNoSituation()
    {
        Assert.Equal("", TaskSituation.Describe(Task(IssueStatus.Completed)).Text);
    }
}
