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
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-reviewer-{Guid.NewGuid():N}");
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
        // task-39: the live-branch reconciliation reads from these.
        // PrHeadSha = what pr.Head.Sha returns. LiveBranchSha = what
        // GetBranchHeadShaAsync(LiveBranch) returns. A null
        // LiveBranchSha simulates a 404 (the dispatcher must fall
        // back to PrHeadSha).
        public string PrHeadSha = "abc123";
        public string? LiveBranchSha = "abc123";
        public string LiveBranch = "agent/task-x";
        public int BranchHeadCalls;
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            // Octokit's PullRequest is sealed + init-only; the head
            // SHA is set via the test seam (headShaOverride). The
            // override path is the only one in tests today.
            // task-39: new tests pass no headShaOverride and let the
            // live-head reconciliation drive headSha — so the PR
            // object must carry a real Head.Sha (or the dispatcher
            // NREs on pr.Head.Sha before the override would have
            // been consulted). GitReference is also sealed + init-only;
            // the public ctor + property setters aren't usable here.
            // Use the long-arg ctor + assign Head afterwards.
            var pr = new PullRequest(number);
            SetHeadSha(pr, PrHeadSha);
            return Task.FromResult(pr);
        }

        private static void SetHeadSha(PullRequest pr, string sha)
        {
            // Reflection: GitReference.Ref/Sha have protected setters,
            // and PullRequest.Head is private-set. Set them via the
            // test's own state-mutation seam. Octokit's API surface
            // doesn't expose a public setter, so this is the
            // minimum-impact way to make the test fixture usable.
            var prType = typeof(PullRequest);
            var headProp = prType.GetProperty("Head");
            var refType = typeof(Octokit.GitReference);
            var refInstance = (Octokit.GitReference)System.Activator.CreateInstance(refType, nonPublic: true)!;
            var shaProp = refType.GetProperty("Sha");
            shaProp!.SetValue(refInstance, sha);
            var labelProp = refType.GetProperty("Label");
            labelProp!.SetValue(refInstance, "fake-head");
            headProp!.SetValue(pr, refInstance);
        }
        public override Task<string> GetPullRequestDiffAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult("diff --git a/F.cs b/F.cs\n+added");
        public override Task<long> CreateIssueCommentAsync(long issueNumber, string body, CancellationToken cancellationToken = default)
        {
            CommentCalls++;
            return Task.FromResult(1L);
        }
        public override Task<long> SubmitReviewAsync(int prNumber, string commitSha, string body, PullRequestReviewState state, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);
        public override Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default)
        {
            BranchHeadCalls++;
            if (LiveBranchSha is null)
            {
                throw new InvalidOperationException("simulated 404 on branch ref");
            }
            return Task.FromResult(LiveBranchSha);
        }
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
            Metadata: new Dictionary<string, object> { ["reworkForSha"] = "abc123" }));
        var watch = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: AgentTaskTypes.PrWatch, Title: "watch",
            Metadata: new Dictionary<string, object> { ["prNumber"] = 7, ["taskId"] = task.Id }));

        var outcome = await dispatcher.ReviewOnceAsync(
            (await _issues.GetAsync(watch.Id))!, headShaOverride: _ => "abc123");

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
    public async Task ReviewOnce_LiveBranchHeadNewer_ReReviewsAgainstLiveHead()
    {
        // task-39 (2026-07-29): the reviewer was re-fired against a
        // cached PR head that still showed `nul` as a tracked file,
        // even though the live origin/agent/task-6 branch had already
        // deleted it (force-push ahead of the PR object). The
        // dispatcher now cross-checks origin/<branch> and prefers
        // the live SHA when it differs. Verdict must be recorded
        // against the LIVE head, not the stale PR head.
        var gh = new FakeGitHub
        {
            // PR object lags the branch tip (the live scenario).
            PrHeadSha = "pr-obj-old",
            LiveBranchSha = "live-new",
            LiveBranch = "agent/task-6",
        };
        var runner = new ScriptedRunner("REVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: AgentTaskTypes.PrWatch, Title: "watch",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = 7,
                ["taskId"] = "task-1",
                ["branch"] = "agent/task-6",
            }));

        var outcome = await dispatcher.ReviewOnceAsync(
            (await _issues.GetAsync(watch.Id))!);

        Assert.NotNull(outcome);
        Assert.Equal("live-new", outcome!.HeadSha);
        var after = (await _issues.GetAsync(watch.Id))!;
        Assert.Equal("live-new", after.GetMetadata("reviewSha"));
        Assert.Equal("Approve", after.GetMetadata("reviewVerdict"));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ReviewOnce_LiveBranchFetchFails_FallsBackToPrHeadSha()
    {
        // Defensive: a 404/timeout on the live branch ref must NOT
        // block review. The dispatcher falls back to pr.Head.Sha and
        // logs at debug.
        var gh = new FakeGitHub
        {
            PrHeadSha = "pr-only",
            LiveBranchSha = null,  // GitHubService throws
            LiveBranch = "agent/task-7",
        };
        var runner = new ScriptedRunner("REVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: AgentTaskTypes.PrWatch, Title: "watch",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = 7,
                ["taskId"] = "task-1",
                ["branch"] = "agent/task-7",
            }));

        var outcome = await dispatcher.ReviewOnceAsync(
            (await _issues.GetAsync(watch.Id))!);

        Assert.NotNull(outcome);
        Assert.Equal("pr-only", outcome!.HeadSha);
        var after = (await _issues.GetAsync(watch.Id))!;
        Assert.Equal("pr-only", after.GetMetadata("reviewSha"));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ReviewOnce_LiveHeadMatchesPrHead_DedupesAsBefore()
    {
        // The reconciliation must not BREAK the common case: when
        // pr.Head.Sha and origin/<branch> agree, the existing per-SHA
        // dedupe path is unchanged.
        var gh = new FakeGitHub
        {
            PrHeadSha = "same-sha",
            LiveBranchSha = "same-sha",
            LiveBranch = "agent/task-8",
        };
        var runner = new ScriptedRunner("REVIEWER_VERDICT: APPROVE");
        var dispatcher = new ReviewerDispatcher(_issues, gh, runner, NullLogger<ReviewerDispatcher>.Instance);
        var watch = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: AgentTaskTypes.PrWatch, Title: "watch",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = 7,
                ["taskId"] = "task-1",
                ["branch"] = "agent/task-8",
            }));

        await dispatcher.ReviewOnceAsync((await _issues.GetAsync(watch.Id))!);
        var second = await dispatcher.ReviewOnceAsync((await _issues.GetAsync(watch.Id))!);

        Assert.Null(second); // deduped: live head unchanged
        Assert.Equal(1, runner.Calls);
    }
}
