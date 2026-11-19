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
    public void LimitedDiscussionOffersParticipantAction()
    {
        var situation = TaskSituation.Describe(Task(IssueStatus.InProgress,
            ("prNumber", "42"), ("reviewMergeState", "BLOCKED"), ("reviewUnresolvedThreads", "1"),
            ("reviewHumanThreads", "1"), ("reviewConversationInputLimited", "1")));
        Assert.Equal("Review discussion needs a participant", situation.Label);
        Assert.Equal("action", situation.Tone);
        Assert.Contains("GitHub", situation.Text);
    }

    [Fact]
    public void FailedVerify_IsActionable_WithNextStep()
    {
        var s = TaskSituation.Describe(Task(IssueStatus.Failed, ("lastError", "pre-push verification failed: `dotnet test` exited 1")));
        Assert.Equal("action", s.Tone);
        Assert.Contains("build/tests failed", s.Text);
        Assert.Contains("retry", s.Text);
    }

    [Fact]
    public void BudgetDenialShowsTheOperatorTheRecordedReservation()
    {
        const string reason = "The request needs a $4.20 reservation. $3.70 is available.";
        var result = TaskSituation.Describe(Task(IssueStatus.Blocked,
            ("blockedKind", "review-integration"), ("reviewSha", "head"),
            ("reviewBudgetDeniedHead", "head"), ("reviewIntegrationError", reason)));
        Assert.Equal("Review paused for budget", result.Label);
        Assert.Equal(reason, result.Text);
        Assert.Equal("Operator", result.NextActor);
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

    [Theory]
    [InlineData("reviewHumanProvenancePending", "Fresh approval required", "@reviewer")]
    [InlineData("reviewAutomatedProvenancePending", "Fresh automated review required", "Forge reviewer")]
    public void EarlierApprovalWithUnknownRevisionIdentifiesTheReviewer(string field, string label, string actor)
    {
        var issue = Task(IssueStatus.InProgress, ("state", "MergeReady"), (field, "True"),
            ("reviewProtectionDecision", "Approved"), ("reviewUnresolvedThreads", "1"));
        var situation = TaskSituation.Describe(issue, nominatedHuman: "reviewer");
        Assert.Equal(label, situation.Label);
        Assert.Equal(actor, situation.NextActor);
        Assert.Contains("1 review conversation is open.", situation.Text);
    }

    [Fact]
    public void ConversationsAccompanyApprovalButDoNotHideReworkOrCompletion()
    {
        var approved = TaskSituation.Describe(Task(IssueStatus.InProgress, ("awaitingHumanReview", "true"),
            ("reviewProtectionDecision", "Approved"), ("reviewUnresolvedThreads", "1"), ("reviewHumanThreads", "1")));
        Assert.Equal("Review conversations open", approved.Label);
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

    [Fact]
    public void MergedHumanReviewUsesTheMergedRevisionAndRecordedReviewer()
    {
        var situation = TaskSituation.Describe(MergedHumanReview(), nominatedHuman: "new-reviewer");
        Assert.Equal("Merged", situation.Label);
        Assert.Equal("Approved by @original-reviewer. No further review action.", situation.HumanReviewSummary);
        Assert.Equal("original-reviewer", situation.NominatedHuman);
        Assert.Null(situation.NextActor);
        Assert.True(situation.HumanReviewRequired);
    }

    [Fact]
    public void UnnominatedCompletedReviewDoesNotInventAnApprover()
    {
        var situation = TaskSituation.Describe(MergedHumanReview("humanReviewNominee", ""),
            nominatedHuman: "new-reviewer");
        Assert.Equal("Human review approved. No further review action.", situation.HumanReviewSummary);
        Assert.DoesNotContain("new-reviewer", situation.HumanReviewSummary);
    }

    [Theory]
    [InlineData("reviewHumanPending", null)]
    [InlineData("reviewHumanPending", "true")]
    [InlineData("reviewHumanPending", "invalid")]
    [InlineData("reviewHumanProvenancePending", null)]
    [InlineData("reviewHumanProvenancePending", "true")]
    [InlineData("humanReviewSyncPending", "true")]
    [InlineData("humanReviewSyncPending", null)]
    [InlineData("awaitingHumanReview", "true")]
    [InlineData("reviewProtectionHead", "old-head")]
    [InlineData("reviewProtectionDecision", "ChangesRequested")]
    [InlineData("reviewProtectionDecision", "Unavailable")]
    [InlineData("reviewUnresolvedThreads", "1")]
    [InlineData("reviewUnresolvedThreads", null)]
    [InlineData("reviewGateError", "GitHub unavailable")]
    [InlineData("lastEvent", "ExternallyMerged")]
    [InlineData("lastEvent", null)]
    [InlineData("observedMerge", "invalid-json")]
    [InlineData("observedMerge", null)]
    [InlineData("prNumber", "99")]
    public void UnprovenTerminalReviewDoesNotClaimApproval(string key, string? value)
    {
        var situation = TaskSituation.Describe(MergedHumanReview(key, value));
        Assert.Equal("No further review action.", situation.HumanReviewSummary);
        Assert.Null(situation.NextActor);
    }

    [Fact]
    public void MergeEvidenceForAnotherTaskCannotEstablishApproval()
    {
        var issue = MergedHumanReview() with { Id = "other-task" };
        Assert.False(TaskSituation.HasObservedMerge(issue));
        Assert.Equal("No further review action.", TaskSituation.Describe(issue).HumanReviewSummary);
    }

    [Fact]
    public void ClosedTaskDoesNotClaimTheRecordedPreMergeApproval()
    {
        var situation = TaskSituation.Describe(MergedHumanReview() with { Status = IssueStatus.Closed });
        Assert.Equal("Closed", situation.Label);
        Assert.Equal("No further review action.", situation.HumanReviewSummary);
        Assert.Null(situation.NextActor);
    }

    [Fact]
    public void ActiveHumanReviewKeepsTheRequiredAction()
    {
        var issue = Task(IssueStatus.InProgress, ("humanReviewRequired", "true"),
            ("awaitingHumanReview", "true"), ("reviewHumanPending", "true"));
        var situation = TaskSituation.Describe(issue, nominatedHuman: "reviewer");
        Assert.Equal("Waiting for human review", situation.Label);
        Assert.Equal("@reviewer", situation.NextActor);
        Assert.True(situation.HumanReviewRequired);
        Assert.Null(situation.HumanReviewSummary);
    }

    [Fact]
    public void TerminalTaskWithoutHumanRequirementDoesNotAddAReviewSummary()
    {
        Assert.Null(TaskSituation.Describe(MergedHumanReview("humanReviewRequired", "false")).HumanReviewSummary);
    }

    private static IssueRecord MergedHumanReview(string? field = null, string? value = null)
    {
        var head = new string('a', 40);
        var metadata = new Dictionary<string, object>
        {
            ["humanReviewRequired"] = true,
            ["humanReviewNominee"] = "original-reviewer",
            ["humanReviewSyncPending"] = false,
            ["reviewHumanPending"] = false,
            ["reviewHumanProvenancePending"] = false,
            ["awaitingHumanReview"] = false,
            ["reviewProtectionHead"] = head,
            ["reviewProtectionDecision"] = "Approved",
            ["reviewUnresolvedThreads"] = 0,
            ["lastEvent"] = "Merged",
            ["prNumber"] = 42,
            // GitHub may update the branch after worker verification.
            ["branchSha"] = new string('b', 40),
            ["observedMerge"] = System.Text.Json.JsonSerializer.Serialize(
                new Forge.Core.Completion.ObservedMergeEvidence("task-1", 42, head,
                    new string('c', 40), DateTimeOffset.UtcNow)),
        };
        if (field is not null)
        {
            if (value is null) metadata.Remove(field);
            else metadata[field] = value;
        }
        return Task(IssueStatus.Completed) with
        {
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata),
        };
    }
}
