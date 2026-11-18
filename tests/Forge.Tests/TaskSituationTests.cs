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
        Assert.Contains("retry", s.Text);
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
        Assert.Contains("retrying", s.Text);
        Assert.DoesNotContain("×3", s.Text);
    }

    [Fact]
    public void PendingReworkRound_ShowsRoundAndReason()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Pending,
            ("prNumber", "742"), ("reworkAttempts", "2"), ("reworkReason", "CI failed for abc: Failure")));
        Assert.Equal("info", s.Tone);
        Assert.Equal("Queued", s.Label);
        Assert.Contains("CI failed", s.Text);
        Assert.DoesNotContain("R2/3", s.Text);
    }

    [Theory]
    [InlineData(TaskEvent.ReviewApproved)]
    [InlineData(TaskEvent.CiGreen)]
    public async System.Threading.Tasks.Task MergeReadyFromOneObservedGate_DoesNotClaimBothPassed(TaskEvent observed)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("merge-situation"));
        var issue = await issues.CreateAsync(new NewIssue("task", "Await merge checks",
            Metadata: new Dictionary<string, object> { ["state"] = "PROpen", ["prNumber"] = "139" }));
        await issues.TransitionAsync(issue.Id, IssueStatus.InProgress, null);
        issue = (await issues.GetAsync(issue.Id))!;
        var machine = new TaskStateMachine(true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await machine.ReportAsync(issues, issue, observed, null, false, CancellationToken.None);
        var after = (await issues.GetAsync(issue.Id))!;
        Assert.Equal("MergeReady", after.GetMetadata("state"));
        Assert.Equal(observed.ToString(), after.GetMetadata("lastEvent"));
        Assert.Null(after.GetMetadata("stateViolation"));
        var situation = TaskSituation.Describe(after);
        Assert.Equal("Final merge checks", situation.Label);
        Assert.Contains("checking", situation.Text);
        Assert.Equal("info", situation.Tone);
    }

    [Fact]
    public void InProgressPROpenApproved_ShowsMergeGateWait()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "PROpen"), ("reviewVerdict", "Approve")));
        Assert.Contains("approved", s.Text);
        Assert.Contains("merge gate", s.Text);
    }

    [Theory]
    [InlineData("BEHIND", "Branch update required", "Forge")]
    [InlineData("DIRTY", "Merge conflict", "Forge")]
    [InlineData("BLOCKED", "GitHub merge requirement pending", "PR participants")]
    [InlineData("DRAFT", "Draft pull request", "PR author")]
    [InlineData("UNKNOWN", "Checking merge requirements", "Forge")]
    [InlineData("Unavailable", "Checking merge requirements", "Forge")]
    [InlineData("CLEAN", "Final merge checks", "Forge")]
    public void MergeRequirementIdentifiesNextAction(string mergeState, string label, string actor)
    {
        var situation = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "MergeReady"), ("reviewMergeState", mergeState)));
        Assert.Equal(label, situation.Label);
        Assert.Equal(actor, situation.NextActor);
    }

    [Theory]
    [InlineData("1", "0", "Forge reviewer", "1 review conversation is open.")]
    [InlineData("2", "1", "PR participants", "2 review conversations are open.")]
    public void OutstandingConversationsIdentifyWhoActs(string count, string human, string actor, string summary)
    {
        var situation = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "MergeReady"),
            ("reviewMergeState", "BLOCKED"), ("reviewUnresolvedThreads", count), ("reviewHumanThreads", human)));
        Assert.Equal("Review conversations open", situation.Label);
        Assert.StartsWith(summary, situation.Text);
        Assert.Equal(actor, situation.NextActor);
    }

    [Fact]
    public void ConversationsAccompanyApprovalButDoNotHideReworkOrCompletion()
    {
        var situation = TaskSituation.Describe(Task(IssueStatus.InProgress, ("awaitingHumanReview", "true"),
            ("reviewUnresolvedThreads", "1"), ("reviewProtectionDecision", "ReviewRequired")));
        Assert.Equal("GitHub approval required", situation.Label);
        Assert.Contains("1 review conversation is open.", situation.Text);
        Assert.Equal("Making a requested change", TaskSituation.Describe(Task(IssueStatus.InProgress,
            ("state", "ReworkRunning"), ("reviewUnresolvedThreads", "1"))).Label);
        Assert.Equal("Completion recorded", TaskSituation.Describe(Task(IssueStatus.Completed,
            ("reviewMergeState", "BLOCKED"), ("reviewUnresolvedThreads", "1"))).Label);
    }

    [Fact]
    public void StalledRework_ExplainsWatcherRefire()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.InProgress, ("state", "StalledRework")));
        Assert.Equal("warn", s.Tone);
        Assert.Contains("re-fires", s.Text);
    }

    [Fact]
    public void CompletedWithoutMergeEvidence_IsExplicitAboutRecordedCompletion()
    {
        Assert.Equal("Completion recorded", TaskSituation.Describe(Task(IssueStatus.Completed)).Label);
    }
}
