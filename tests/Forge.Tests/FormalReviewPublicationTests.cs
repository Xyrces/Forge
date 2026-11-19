using Forge.Agents;
using Forge.Core;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using NewIssue = Forge.Core.NewIssue;
using Xunit;

namespace Forge.Tests;

public sealed class FormalReviewPublicationTests
{
    [Theory]
    [InlineData("Looks approved", ReviewerVerdict.Error)]
    [InlineData("REVIEWER_VERDICT: APPROVE\nMore text", ReviewerVerdict.Error)]
    [InlineData("Do not REQUEST_CHANGES for nits\nREVIEWER_VERDICT: APPROVE", ReviewerVerdict.Approve)]
    [InlineData("REVIEWER_VERDICT: REQUEST_CHANGES\n", ReviewerVerdict.RequestChanges)]
    public void VerdictMustBeExplicitAndTerminal(string output, ReviewerVerdict expected) => Assert.Equal(expected, ReviewFindings.ParseVerdict(output));

    [Fact]
    public void FindingsUseExactDiffSideAndUnanchorableFindingsStayInSummary()
    {
        const string diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -5,2 +5,2 @@\n-old\n+new\n context\n";
        var output = "REVIEWER_FINDING: {\"path\":\"a.cs\",\"line\":5,\"side\":\"LEFT\",\"body\":\"Removed check is needed\"}\n"
            + "REVIEWER_FINDING: {\"path\":\"a.cs\",\"line\":400,\"side\":\"RIGHT\",\"body\":\"Cross-file issue\"}\nREVIEWER_VERDICT: REQUEST_CHANGES";
        var draft = ReviewFindings.Create(2, "head", "RequestChanges", output, diff);
        Assert.Single(draft.Findings);
        Assert.Equal("LEFT", draft.Findings[0].Side);
        Assert.Contains("Cross-file issue", draft.Summary);
        Assert.DoesNotContain("REVIEWER_VERDICT", draft.Summary);
        Assert.NotEqual(ReviewFindings.Marker(draft), ReviewFindings.Marker(draft with { HeadSha = "other" }));
    }

    private sealed class Runner : IAgentRunner
    {
        public int Calls;
        public Exception? Failure;
        public string Output = "Assessment\nREVIEWER_VERDICT: APPROVE";
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context = null, CancellationToken ct = default)
        { Calls++; if (Failure is not null) throw Failure; return Task.FromResult(new AgentRunResult(Output, null, 0, 0, TimeSpan.Zero)); }
    }

    [Fact]
    public async Task InvalidModelVerdictsCannotSpendIndefinitelyAtSameHead()
    {
        var directory = TempRoot.Instance.NewDirectory("review-errors");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["worktreePath"] = directory }));
        var runner = new Runner { Output = "No usable verdict" };
        var dispatcher = new ReviewerDispatcher(issues, new PublicationGitHub(), runner, NullLogger<ReviewerDispatcher>.Instance);
        for (var i = 0; i < 6; i++) await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        Assert.Equal(3, runner.Calls);
        Assert.Equal("3", (await issues.GetAsync(task.Id))!.GetMetadata("reviewErrorAttempts"));
    }

    [Fact]
    public async Task ConfirmedBudgetDenialStopsAfterOneAttemptUntilOperatorRetry()
    {
        var directory = TempRoot.Instance.NewDirectory("review-budget-denied");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["worktreePath"] = directory }));
        var runner = new Runner { Failure = new Forge.Core.Control.BudgetAdmissionDeniedException(4.2m, 3.7m) };
        var github = new PublicationGitHub();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        for (var i = 0; i < 4; i++) await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        var denied = (await issues.GetAsync(task.Id))!;
        Assert.Equal(1, runner.Calls);
        Assert.True(ReviewIntegrationState.Exhausted(denied, "head"));
        Assert.False(ReviewIntegrationState.Exhausted(denied, "new-head"));
        Assert.Equal("1", denied.GetMetadata("reviewErrorAttempts"));
        Assert.Contains("not billed usage", denied.GetMetadata("reviewIntegrationError"));
        Assert.Null(denied.GetMetadata("reviewDraft"));
        Assert.Equal(0, github.Publications);
        await issues.TransitionAsync(task.Id, denied.Status, "operator retry", ReviewIntegrationState.RetryMetadata());
        await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        Assert.Equal(2, runner.Calls);
    }

    private sealed class PublicationGitHub : GitHubService
    {
        public PublicationGitHub() : base("o", "r", null) { }
        public override string? ReviewerLogin => "reviewer[bot]";
        public MergeRequirements Requirements = new("CLEAN", []);
        public int Resolutions;
        public int HumanEvidenceReplies;
        public bool LoseEvidenceReplyResponse;
        public override Task<MergeRequirements> GetMergeRequirementsAsync(int number, string head, CancellationToken ct = default)
            => Task.FromResult(Requirements);
        public int DiscussionLoads;
        public override Task<IReadOnlyList<ReviewConversation>> GetReviewConversationInputsAsync(
            IReadOnlyList<ReviewConversation> observations, string head, CancellationToken ct = default)
        {
            DiscussionLoads++;
            return Task.FromResult(observations);
        }
        public override Task<IReadOnlyDictionary<string, string>> ResolveReviewedConversationsAsync(int number, ReviewDraft draft, CancellationToken ct = default)
        {
            if (draft.EvidenceReplies is { Count: > 0 } && !Requirements.Conversations.All(t => t.LastBody == ReviewFindings.EvidenceMarker(draft, t.Id)))
            {
                HumanEvidenceReplies++;
                Requirements = new("BLOCKED", Requirements.Conversations.Select(t => t with
                {
                    LastCommentId = "evidence-reply", LastAuthor = ReviewerLogin,
                    LastBody = ReviewFindings.EvidenceMarker(draft, t.Id),
                    Body = "Original finding\n\n" + ReviewFindings.EvidenceMarker(draft, t.Id)
                }).ToArray());
                if (LoseEvidenceReplyResponse) throw new IOException("Accepted reply response lost");
            }
            if (draft.Resolutions is { Count: > 0 })
            {
                Resolutions++;
                Requirements = new("CLEAN", []);
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(Requirements.Conversations
                .Where(t => draft.EvidenceReplies?.Any(r => r.ThreadId == t.Id) == true).ToDictionary(t => t.Id, t => t.Version));
        }
        public int Publications;
        public bool MissingIdentity, DiscoverPublished = true;
        public bool RejectPublication, LoseResponse = true;
        public int RejectStatus;
        public int Accepted;
        private PublishedReview? _published;
        private string? _publishedMarker;
        public override Task EnsureReviewerIdentityAsync(int pr, CancellationToken ct) => MissingIdentity ? Task.FromException(new InvalidOperationException("missing reviewer App")) : Task.CompletedTask;
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default) => Task.FromResult(new PullRequest(number));
        public override Task<string> GetPullRequestDiffAsync(int number, CancellationToken cancellationToken = default) => Task.FromResult("");
        public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PullRequestReview>>([]);
        public override Task<IReadOnlyList<PrComment>> GetIssueCommentsAsync(int number, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PrComment>>([]);
        public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default) => Task.FromResult(CommitState.Success);
        public override Task<PublishedReview?> FindPublishedReviewAsync(int pr, ReviewDraft draft, string marker, CancellationToken ct) => Task.FromResult(DiscoverPublished && _publishedMarker == marker ? _published : null);
        public override Task<PublishedReview> PublishFormalReviewAsync(int pr, ReviewDraft draft, string marker, CancellationToken ct)
        {
            Publications++;
            if (RejectStatus != 0) throw new ApiException("Fixture refusal", (System.Net.HttpStatusCode)RejectStatus);
            if (RejectPublication) throw new ApiValidationException();
            Accepted++;
            _publishedMarker = marker;
            _published = new(draft.Round, draft.HeadSha, draft.Verdict, draft.Summary, ReviewerLogin!, "https://github.com/o/r/pull/7#pullrequestreview-1", DateTimeOffset.UtcNow, draft.Findings);
            return LoseResponse ? throw new IOException("Response lost after GitHub accepted review") : Task.FromResult(_published);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UncertainPublicationReconcilesWithoutAnotherModelRunOrPost(bool visible)
    {
        var directory = TempRoot.Instance.NewDirectory("formal-review");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object> { ["prNumber"] = 7, ["worktreePath"] = directory }));
        var github = new PublicationGitHub { DiscoverPublished = visible };
        var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        Assert.Equal(ReviewerVerdict.Error, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.NotNull((await issues.GetAsync(task.Id))!.GetMetadata("reviewDraft"));
        var next = await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        Assert.Equal(visible ? ReviewerVerdict.Approve : ReviewerVerdict.Error, next!.Verdict);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(1, github.Publications);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupersededUncertainDraftIsArchivedBeforeNewHeadReview(bool oldPublicationVisible)
    {
        var directory = TempRoot.Instance.NewDirectory("superseded-review");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["worktreePath"] = directory }));
        var github = new PublicationGitHub();
        var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        Assert.Equal(ReviewerVerdict.Error, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "old"))!.Verdict);
        github.DiscoverPublished = oldPublicationVisible;
        github.LoseResponse = false;
        Assert.Equal(ReviewerVerdict.Approve, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "new"))!.Verdict);
        var after = (await issues.GetAsync(task.Id))!;
        Assert.Null(after.GetMetadata("reviewDraft"));
        Assert.Null(after.GetMetadata("reviewPublishStarted"));
        Assert.Equal("new", after.GetMetadata("reviewPublishedHead"));
        Assert.Contains("old", after.GetMetadata("reviewSupersededDraft"));
        Assert.Equal(oldPublicationVisible ? "published" : "unconfirmed", after.GetMetadata("reviewSupersededPublication"));
        Assert.Equal(2, runner.Calls);
        Assert.Equal(2, github.Publications);
        if (oldPublicationVisible) Assert.Contains("old", after.GetMetadata("reviewHistory"));
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "new"));
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task PreviouslyApprovedHeadReviewsNewConversationsOnceAndDeduplicatesUnchangedEvidence()
    {
        var directory = TempRoot.Instance.NewDirectory("same-head-threads");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        {
            ["prNumber"] = 7, ["worktreePath"] = directory, ["reviewSha"] = "head",
            ["reviewVerdict"] = "Approve", ["reviewPublishedHead"] = "head"
        }));
        var thread = new ReviewConversation("thread", false, true, "File.cs", "reviewer[bot]", "comment", false, "Original concern");
        var github = new PublicationGitHub { LoseResponse = false, Requirements = new("BLOCKED", [thread]) };
        var runner = new Runner { Output = "REVIEWER_RESOLVED_THREAD: {\"threadId\":\"thread\",\"lastCommentId\":\"comment\",\"reason\":\"Verified the new guard and regression\"}\nREVIEWER_VERDICT: APPROVE" };
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        Assert.Equal(ReviewerVerdict.Approve, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(1, github.Resolutions);
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        github.Requirements = new("BLOCKED", [thread]);
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        Assert.Equal(1, runner.Calls);
        github.Requirements = new("BLOCKED", [thread with { Author = "human", LastCommentId = "new-human-comment" }]);
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        Assert.Equal(1, runner.Calls);
        github.Requirements = new("BLOCKED", [thread with { LastCommentId = "receipt", Body = "Reopened after verification" }]);
        runner.Output = runner.Output.Replace("\"lastCommentId\":\"comment\"", "\"lastCommentId\":\"receipt\"");
        Assert.Equal(ReviewerVerdict.Approve, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.Equal(2, runner.Calls);
        Assert.Equal(2, github.Resolutions);
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task HumanCorrectionEvidenceUsesSavedReviewAndDoesNotReopenPaidWork(bool actualCorrection)
    {
        var directory = TempRoot.Instance.NewDirectory("human-thread-evidence");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        {
            ["prNumber"] = 7, ["worktreePath"] = directory, ["reviewSha"] = "head",
            ["reviewVerdict"] = "Approve", ["reviewPublishedHead"] = "head"
        }));
        var thread = new ReviewConversation("human", false, true, "File.cs", "alice", "comment", false,
            "Please fix validation", OriginalHead: "old-head");
        var github = new PublicationGitHub { LoseResponse = false, LoseEvidenceReplyResponse = actualCorrection,
            Requirements = new("BLOCKED", [thread]) };
        var runner = new Runner
        {
            Output = (actualCorrection ? "REVIEWER_ADDRESSED_THREAD: {\"threadId\":\"human\",\"lastCommentId\":\"comment\",\"reason\":\"Verified guard and regression\"}\n" : "")
                + "REVIEWER_VERDICT: APPROVE"
        };
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        var first = await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        Assert.Equal(actualCorrection ? ReviewerVerdict.Error : ReviewerVerdict.Approve, first!.Verdict);
        if (actualCorrection)
        {
            Assert.NotNull((await issues.GetAsync(task.Id))!.GetMetadata("reviewDraft"));
            Assert.Equal(ReviewerVerdict.Approve, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        }
        for (var i = 0; i < 3; i++) Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        Assert.Equal(1, runner.Calls);
        Assert.Equal(1, github.Publications);
        Assert.Equal(actualCorrection ? 1 : 0, github.HumanEvidenceReplies);
        Assert.Equal(1, github.DiscussionLoads);
        Assert.Equal(0, github.Resolutions);
        Assert.Equal(1, github.Requirements.Unresolved);
    }

    [Fact]
    public async Task MissingReviewerIdentityStopsBeforePaidWork()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("missing-reviewer"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object> { ["prNumber"] = 7 }));
        var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, new PublicationGitHub { MissingIdentity = true }, runner, NullLogger<ReviewerDispatcher>.Instance);
        var result = await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        Assert.Equal(ReviewerVerdict.Error, result!.Verdict);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PublicationFaultHasBoundedPostsAndOperatorRetryKeepsDraft(bool uncertain)
    {
        var directory = TempRoot.Instance.NewDirectory("publication-recovery");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["worktreePath"] = directory, ["reworkAttempts"] = 2 }));
        var github = new PublicationGitHub { RejectPublication = !uncertain, DiscoverPublished = false };
        var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        for (var i = 0; i < 5; i++) await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        task = (await issues.GetAsync(task.Id))!;
        Assert.True(ReviewIntegrationState.Exhausted(task, "head"));
        Assert.Equal(uncertain ? 1 : 3, github.Publications);
        Assert.Equal(1, runner.Calls);
        await issues.TransitionAsync(task.Id, IssueStatus.Blocked, "integration fault",
            new Dictionary<string, object> { ["blockedKind"] = ReviewIntegrationState.BlockedKind });
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        // Normal endpoint applies these exact metadata resets, preserving uncertain intent.
        await issues.TransitionAsync(task.Id, IssueStatus.InProgress, "operator retry", ReviewIntegrationState.RetryMetadata());
        if (uncertain)
        {
            Assert.Equal(ReviewerVerdict.Error, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
            Assert.Equal(1, github.Publications);
            await issues.TransitionAsync(task.Id, IssueStatus.InProgress, "operator retry", ReviewIntegrationState.RetryMetadata());
        }
        github.RejectPublication = false; github.DiscoverPublished = true; github.LoseResponse = false;
        Assert.Equal(ReviewerVerdict.Approve, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.Equal(uncertain ? 1 : 4, github.Publications);
        Assert.Equal(1, runner.Calls);
        Assert.Equal("2", (await issues.GetAsync(task.Id))!.GetMetadata("reworkAttempts"));
    }

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(422)] [InlineData(429)]
    public async Task DefinitePermissionOrValidationRefusalCanRecoverSameDraftWithoutPaidRerun(int status)
    {
        var directory = TempRoot.Instance.NewDirectory("refused-review");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["worktreePath"] = directory }));
        var github = new PublicationGitHub { RejectStatus = status, LoseResponse = false };
        var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        for (var i = 0; i < 5; i++) await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        var refused = (await issues.GetAsync(task.Id))!;
        Assert.Equal("False", refused.GetMetadata("reviewPublishStarted"));
        Assert.Equal(3, github.Publications); Assert.Equal(0, github.Accepted); Assert.Equal(1, runner.Calls);
        var draft = refused.GetMetadata("reviewDraft");
        await issues.TransitionAsync(task.Id, IssueStatus.Blocked, "review refused", new Dictionary<string, object> { ["blockedKind"] = ReviewIntegrationState.BlockedKind });
        await issues.TransitionAsync(task.Id, IssueStatus.InProgress, "operator repaired permissions", ReviewIntegrationState.RetryMetadata());
        Assert.Equal(draft, (await issues.GetAsync(task.Id))!.GetMetadata("reviewDraft"));
        github.RejectStatus = 0;
        Assert.Equal(ReviewerVerdict.Approve, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        Assert.Equal(1, runner.Calls); Assert.Equal(1, github.Accepted); Assert.Equal(4, github.Publications);
    }

    [Theory]
    [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public void ServerFailuresRemainAmbiguous(int status) =>
        Assert.False(ReviewerDispatcher.IsDefinitePublicationRefusal(new ApiException("Server fault", (System.Net.HttpStatusCode)status)));

    [Fact]
    public async Task RepairingServerFailureDoesNotAuthorizeBlindResubmission()
    {
        var directory = TempRoot.Instance.NewDirectory("ambiguous-server-review");
        await using var issues = new IssueStore(Path.Combine(directory, "state.db"));
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["worktreePath"] = directory }));
        var github = new PublicationGitHub { RejectStatus = 503, LoseResponse = false, DiscoverPublished = false };
        var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head");
        Assert.Equal("True", (await issues.GetAsync(task.Id))!.GetMetadata("reviewPublishStarted"));
        await issues.TransitionAsync(task.Id, IssueStatus.InProgress, "operator retry", ReviewIntegrationState.RetryMetadata());
        github.RejectStatus = 0;
        Assert.Equal(ReviewerVerdict.Error, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.Equal(1, runner.Calls); Assert.Equal(1, github.Publications); Assert.Equal(0, github.Accepted);
        Assert.True(ReviewIntegrationState.Exhausted((await issues.GetAsync(task.Id))!, "head"));
    }

    [Theory]
    [InlineData("Error")] [InlineData("unknown")] [InlineData("999")]
    public async Task InvalidSavedDraftVerdictStopsWithoutPaidWorkOrSubmission(string verdict)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("invalid-draft"));
        var draft = new ReviewDraft(1, "head", verdict, "Unusable saved draft", []);
        var task = await issues.CreateAsync(new NewIssue("client", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["reviewDraft"] = System.Text.Json.JsonSerializer.Serialize(draft, ReviewFindings.Json) }));
        var github = new PublicationGitHub(); var runner = new Runner();
        var dispatcher = new ReviewerDispatcher(issues, github, runner, NullLogger<ReviewerDispatcher>.Instance);
        Assert.Equal(ReviewerVerdict.Error, (await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"))!.Verdict);
        Assert.True(ReviewIntegrationState.Exhausted((await issues.GetAsync(task.Id))!, "head"));
        Assert.Null(await dispatcher.ReviewOnceAsync(task, headShaOverride: _ => "head"));
        Assert.Equal(0, runner.Calls); Assert.Equal(0, github.Publications);
    }
}
