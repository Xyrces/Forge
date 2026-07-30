using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// ReviewerDispatcher.ReviewOnceAsync: runs the Reviewer role over
/// the PR diff, records the verdict in the watch metadata (the
/// machine record PRWatcher merges on), and dedupes per head SHA.
/// </summary>
public class ReviewerDispatcherTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;

    public ReviewerDispatcherTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("reviewer");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private sealed class FakeGitHub : GitHubService
    {
        public int CommentCalls;
        public (string Base, string Head)? CompareCall;
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult(new PullRequest(number));
        public override Task<string> GetPullRequestDiffAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult("diff --git a/F.cs b/F.cs\n+added");
        public override Task<string> GetCompareDiffAsync(string baseSha, string headSha, CancellationToken cancellationToken = default)
        {
            CompareCall = (baseSha, headSha);
            return Task.FromResult("INCREMENTAL-DIFF");
        }
        public override Task<long> CreateIssueCommentAsync(long issueNumber, string body, CancellationToken cancellationToken = default)
        {
            CommentCalls++;
            return Task.FromResult(1L);
        }
        public override Task<long> SubmitReviewAsync(int prNumber, string commitSha, string body, PullRequestReviewState state, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);
    }

    private sealed class ScriptedRunner : IAgentRunner
    {
        private readonly string _response;
        public int Calls;
        public ScriptedRunner(string response) { _response = response; }
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AgentRunResult(_response, null, 0, 0, TimeSpan.Zero));
        }
    }

    private async Task<IssueRecord> SeedWatchAsync()
    {
        var watch = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: AgentTaskTypes.PrWatch, Title: "watch",
            Metadata: new Dictionary<string, object> { ["prNumber"] = 7, ["taskId"] = "task-1" }));
        return (await _issues.GetAsync(watch.Id))!;
    }

    [Fact]
    public async Task ReviewOnce_RecordsVerdictInWatchMetadata()
    {
        var gh = new FakeGitHub();
        var runner = new ScriptedRunner("Looks good.\nREVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await SeedWatchAsync();

        var outcome = await dispatcher.ReviewOnceAsync(watch, headShaOverride: _ => "abc123");

        Assert.NotNull(outcome);
        Assert.Equal(ReviewerVerdict.Approve, outcome!.Verdict);
        var after = (await _issues.GetAsync(watch.Id))!;
        Assert.Equal("Approve", after.GetMetadata("reviewVerdict"));
        Assert.Equal("1", after.GetMetadata("reviewRound"));
        Assert.NotNull(after.GetMetadata("reviewSha"));
        Assert.Equal(1, gh.CommentCalls);
    }

    [Fact]
    public async Task ReviewOnce_SameShaTwice_SkipsSecondReview()
    {
        var gh = new FakeGitHub();
        var runner = new ScriptedRunner("REVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await SeedWatchAsync();

        await dispatcher.ReviewOnceAsync(watch, headShaOverride: _ => "abc123");
        var second = await dispatcher.ReviewOnceAsync((await _issues.GetAsync(watch.Id))!, headShaOverride: _ => "abc123");

        Assert.Null(second); // deduped
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ReviewOnce_ReworkInFlightForThisHead_SkipsReview()
    {
        // Token-saving: a rework round is queued/in-flight for this
        // exact head — the dev agent's push will replace it, and the
        // sha-stamped verdict would be discarded. Don't review a head
        // that's about to change. The round record lives on the TASK
        // via the machine (reworkForSha).
        var gh = new FakeGitHub();
        var runner = new ScriptedRunner("REVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var task = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: "task", Title: "t",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = 7,
                ["reworkForSha"] = "abc123",
            }));

        var outcome = await dispatcher.ReviewOnceAsync(
            (await _issues.GetAsync(task.Id))!, headShaOverride: _ => "abc123");

        Assert.Null(outcome);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, gh.CommentCalls);
    }

    [Fact]
    public async Task ReviewOnce_LlmThrows_ErrorVerdict_NoSilentApprove()
    {
        var gh = new FakeGitHub();
        var runner = new ScriptedRunner(""); // ParseReviewerOutput(empty) => Error
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await SeedWatchAsync();

        var outcome = await dispatcher.ReviewOnceAsync(watch, headShaOverride: _ => "abc123");

        Assert.NotNull(outcome);
        Assert.Equal(ReviewerVerdict.Error, outcome!.Verdict);
        var after = (await _issues.GetAsync(watch.Id))!;
        Assert.Equal("Error", after.GetMetadata("reviewVerdict"));
        Assert.Equal(0, gh.CommentCalls); // no comment flood on error
    }

    [Fact]
    public async Task ReviewOnce_ErrorDoesNotDedupe_RetriesNextSweep()
    {
        var gh = new FakeGitHub();
        var runner = new ScriptedRunner("");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await SeedWatchAsync();

        await dispatcher.ReviewOnceAsync(watch, headShaOverride: _ => "abc123");
        var retry = await dispatcher.ReviewOnceAsync((await _issues.GetAsync(watch.Id))!, headShaOverride: _ => "abc123");

        Assert.NotNull(retry); // retried (circuit breaker lives in PRWatcher)
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task ReReview_UsesIncrementalDiffAndPriorFindings()
    {
        // Pause/resume review: a head move after a verdict re-reviews
        // with the INCREMENTAL diff (old..new) plus the prior
        // findings to verify — not the full PR diff.
        var gh = new FakeGitHub();
        var runner = new CapturingRunner("Addressed.\nREVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var task = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: "task", Title: "t",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = 7,
                ["reviewSha"] = "old111aaa",
                ["reviewVerdict"] = "RequestChanges",
                ["reviewNotes"] = "fix F.cs null check",
                ["reviewRound"] = "1",
            }));

        var outcome = await dispatcher.ReviewOnceAsync(
            (await _issues.GetAsync(task.Id))!, headShaOverride: _ => "new222bbb");

        Assert.NotNull(outcome);
        Assert.Equal(("old111aaa", "new222bbb"), gh.CompareCall);
        Assert.Contains("INCREMENTAL-DIFF", runner.Prompt);
        Assert.DoesNotContain("diff --git a/F.cs", runner.Prompt);   // not the full diff
        Assert.Contains("fix F.cs null check", runner.Prompt);       // prior findings to verify
        Assert.Contains("RE-REVIEWING", runner.Prompt);
        // The verdict landing cleared the "reviewing…" marker.
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Null(after.GetMetadata("reviewStartedAt"));
        Assert.Equal("new222bbb", after.GetMetadata("reviewSha"));
        Assert.Equal("2", after.GetMetadata("reviewRound"));
    }

    [Fact]
    public async Task FirstReview_UsesFullDiff_NoIncrementalCall()
    {
        var gh = new FakeGitHub();
        var runner = new CapturingRunner("REVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await SeedWatchAsync();

        await dispatcher.ReviewOnceAsync(watch, headShaOverride: _ => "abc123");

        Assert.Null(gh.CompareCall);
        Assert.Contains("diff --git a/F.cs", runner.Prompt);
    }

    private sealed class CapturingRunner : IAgentRunner
    {
        private readonly string _response;
        public string Prompt { get; private set; } = "";
        public CapturingRunner(string response) { _response = response; }
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context = null, CancellationToken ct = default)
        {
            Prompt = prompt;
            return Task.FromResult(new AgentRunResult(_response, null, 0, 0, TimeSpan.Zero));
        }
    }
}
