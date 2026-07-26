using Forge.Core;
using Forge.Dashboard;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// The review/rework loop: PRWatcher.PollWatchOnceAsync consults CI
/// (GitHub) + the reviewer agent's verdict (watch metadata) and, on
/// failure, requeues the task for a bounded rework round instead of
/// going terminal. Circuit breaker at MaxReworkAttempts.
/// </summary>
public class PRWatcherReworkTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly InMemoryDashboardEventBus _events;

    public PRWatcherReworkTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-rework-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    /// <summary>GitHub fake: scriptable CI state, no real API calls.</summary>
    private sealed class FakeGitHub : GitHubService
    {
        public CommitState Ci = CommitState.Pending;
        public CommitState BaseCi = CommitState.Success;   // base branch (main) head check state
        public IReadOnlyList<string> FailedChecks = Array.Empty<string>();
        public bool MergeResult = true;
        public int MergeCalls;
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult(new PullRequest(number));
        public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(sha == "main-head-sha" ? BaseCi : Ci);
        public override Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default)
            => Task.FromResult("main-head-sha");
        public override Task<IReadOnlyList<string>> GetFailedCheckRunSummariesAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(FailedChecks);
        public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<PullRequestReview>)Array.Empty<PullRequestReview>());
        public override Task<bool> MergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            MergeCalls++;
            return Task.FromResult(MergeResult);
        }
        public override Task<bool> DeleteBranchAsync(string branch, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private PRWatcher NewWatcher(FakeGitHub gh, Forge.Core.StageGates? gates = null) => new(
        gh,
        worktrees: new AgentTools.GitWorktreeService(
            new Configuration.WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<AgentTools.GitWorktreeService>.Instance),
        issues: _issues,
        pollInterval: TimeSpan.FromSeconds(1),
        staleAfter: TimeSpan.FromHours(1),
        events: _events,
        logger: NullLogger<PRWatcher>.Instance,
        gates: gates,
        lifecycle: new Forge.Core.TaskStateMachine(_issues, writeAuthority: false, NullLogger.Instance));

    private async Task<(IssueRecord task, IssueRecord watch)> SeedAsync(
        Dictionary<string, object>? taskMeta = null,
        Dictionary<string, object>? watchMeta = null)
    {
        // Production shape at watch time: the task is InProgress and
        // carries prNumber on its OWN metadata (written by the
        // dispatch executors) — the machine's derivation reads it.
        var tm = taskMeta ?? new Dictionary<string, object>();
        tm["prNumber"] = "42";
        var task = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: "task", Title: "implement X", Metadata: tm));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null);
        var meta = watchMeta ?? new Dictionary<string, object>();
        meta["prNumber"] = 42;
        meta["taskId"] = task.Id;
        meta["branch"] = $"agent/{task.Id}";
        var watch = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: AgentTaskTypes.PrWatch, Title: "watch", Metadata: meta));
        return ((await _issues.GetAsync(task.Id))!, (await _issues.GetAsync(watch.Id))!);
    }

    private static PullRequest Pr(int n) => new(n);

    [Fact]
    public async Task GreenApprovedButConflicting_RoutesToConflictRework_NoMergeAttempt()
    {
        // Observed live 2026-07-26 (PRs #42/#43): green + approved at
        // the current head, but CONFLICTING after a sibling PR merged
        // — Octokit's merge returns false and the watch retried the
        // doomed merge every sweep for 8+ hours (sprint could never
        // complete). The green path must route to the conflict sync
        // round, and must NOT attempt the merge.
        var gh = new FakeGitHub { Ci = CommitState.Success };
        var (task, watch) = await SeedAsync();

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None,
            reviewsOverride: _ => new[] { PullRequestReviewState.Approved },
            headShaOverride: _ => "abc123",
            mergeableOverride: _ => false);

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        Assert.Equal(0, gh.MergeCalls);                              // no doomed merge attempt
        var taskAfter = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Pending, taskAfter.Status);
        Assert.Equal("1", taskAfter.GetMetadata("reworkAttempts"));
        Assert.Contains("conflicts with the base branch", taskAfter.GetMetadata("reworkReason"));
    }

    [Fact]
    public async Task MergeRefusedThenMergeableResolvesFalse_RoutesToConflictRework()
    {
        // Mergeable was null at first read (still computing); the
        // merge attempt 405s; the re-check lands as conflicting.
        var gh = new FakeGitHub { Ci = CommitState.Success, MergeResult = false };
        var (task, watch) = await SeedAsync();
        var calls = 0;

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None,
            reviewsOverride: _ => new[] { PullRequestReviewState.Approved },
            headShaOverride: _ => "abc123",
            mergeableOverride: _ => ++calls == 1 ? null : false);

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        Assert.Equal(1, gh.MergeCalls);                              // one attempt, then routed
        Assert.Contains("conflicts", (await _issues.GetAsync(task.Id))!.GetMetadata("reworkReason"));
    }

    [Fact]
    public async Task WatchedTaskTerminal_ClosesWatch_SkipsEverything()
    {
        // Orphan guard (observed live 2026-07-26: pr-watch-44 kept
        // polling after task-161 + PR #34 were closed — a CI-failure
        // fire would have resurrected the Closed task to Pending).
        var gh = new FakeGitHub { Ci = CommitState.Failure };
        var (task, watch) = await SeedAsync();
        await _issues.TransitionAsync(task.Id, IssueStatus.Closed, "operator closeout");

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(watch.Id))!.Status);
        // The task is untouched — critically, NOT resurrected to Pending.
        Assert.Equal(IssueStatus.Closed, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(0, gh.MergeCalls);
    }

    [Fact]
    public async Task MergeGreen_GateHeld_NoMerge_WatchAndTaskUntouched_ThenReleaseMerges()
    {
        var bootstrap = new Forge.Core.IssueStore(Path.Combine(_workDir, "memory.db"));
        bootstrap.Dispose();
        var gates = new Forge.Core.StageGates(new Forge.Core.MemoryStore(Path.Combine(_workDir, "memory.db")));
        await gates.HoldAsync(Forge.Core.StageGates.Merge);

        var gh = new FakeGitHub { Ci = CommitState.Success };
        var (task, watch) = await SeedAsync();
        var watcher = NewWatcher(gh, gates);

        var held = await watcher.PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => new[] { PullRequestReviewState.Approved },
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, held);
        Assert.Equal(0, gh.MergeCalls);
        // Task untouched (production shape: InProgress while the
        // gate holds the merge).
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(task.Id))!.Status);

        await gates.ReleaseAsync(Forge.Core.StageGates.Merge);
        var released = await watcher.PollWatchOnceAsync(
            await _issues.GetAsync(watch.Id) ?? watch, CancellationToken.None,
            reviewsOverride: _ => new[] { PullRequestReviewState.Approved },
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Merged, released);
        Assert.Equal(1, gh.MergeCalls);
        Assert.Equal(IssueStatus.Completed, (await _issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task ConflictingPr_RequeuesSyncRework_WatchStaysLive()
    {
        // Observed live 2026-07-25 (PR #33): an APPROVED PR with
        // merge conflicts gets no pull_request CI runs at all
        // (GitHub can't build the test merge ref), so the merge gate
        // waits forever. The watcher must dispatch a sync rework
        // round (merge main into the same branch) instead.
        var gh = new FakeGitHub { Ci = CommitState.Pending };   // no CI runs exist for conflicting PRs
        var (task, watch) = await SeedAsync();

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None,
            reviewsOverride: _ => new[] { PullRequestReviewState.Approved },
            headShaOverride: _ => "abc123",
            mergeableOverride: _ => false);

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        var after = await _issues.GetAsync(task.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);       // requeued for the sync round
        Assert.Equal("1", after.GetMetadata("reworkAttempts"));
        Assert.Contains("conflicts with the base branch", after.GetMetadata("reworkReason"));
        Assert.Contains("git merge origin/main", after.GetMetadata("reworkContext"));
        // The round record lives on the TASK via the machine
        // (state + reworkForSha) so the next sweep doesn't
        // re-trigger while the agent works.
        Assert.Equal("ReworkQueued", after.GetMetadata("state"));
        Assert.Equal("abc123", after.GetMetadata("reworkForSha"));
        Assert.Equal(0, gh.MergeCalls);
    }

    [Fact]
    public async Task MergeableNullWhileComputing_DoesNotFireConflictRework()
    {
        // GitHub computes mergeability asynchronously; null must
        // mean "not yet known" (keep polling), never "conflicting".
        var gh = new FakeGitHub { Ci = CommitState.Pending };
        var (task, watch) = await SeedAsync();

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None,
            reviewsOverride: _ => new[] { PullRequestReviewState.Approved },
            headShaOverride: _ => "abc123",
            mergeableOverride: _ => null);

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, outcome);
        var after = await _issues.GetAsync(task.Id);
        Assert.Null(after.GetMetadata("reworkAttempts"));
        Assert.Null(after.GetMetadata("reworkReason"));
        Assert.Equal(0, gh.MergeCalls);
    }

    [Fact]
    public async Task CiFailed_RequeuesTask_WithContext_WatchStaysLive()
    {
        var gh = new FakeGitHub { Ci = CommitState.Failure };
        var (task, watch) = await SeedAsync();

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        var taskAfter = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Pending, taskAfter.Status);
        Assert.Equal("1", taskAfter.GetMetadata("reworkAttempts"));
        Assert.Contains("CI", taskAfter.GetMetadata("reworkContext"));
        var watchAfter = (await _issues.GetAsync(watch.Id))!;
        Assert.Equal(IssueStatus.Pending, watchAfter.Status);
        // Round record on the task via the machine (Phase 3).
        Assert.Equal("abc123", taskAfter.GetMetadata("reworkForSha"));
        Assert.Equal("ReworkQueued", taskAfter.GetMetadata("state"));
    }

    [Fact]
    public async Task CiFailed_SameHeadTwice_NoDoubleRework()
    {
        var gh = new FakeGitHub { Ci = CommitState.Failure };
        var (task, watch) = await SeedAsync();
        var watcher = NewWatcher(gh);

        await watcher.PollWatchOnceAsync(watch, CancellationToken.None,
            reviewsOverride: _ => Array.Empty<PullRequestReviewState>(), headShaOverride: _ => "abc123");
        var watchAfter = (await _issues.GetAsync(watch.Id))!;
        var second = await watcher.PollWatchOnceAsync(watchAfter, CancellationToken.None,
            reviewsOverride: _ => Array.Empty<PullRequestReviewState>(), headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, second);
        Assert.Equal("1", (await _issues.GetAsync(task.Id))!.GetMetadata("reworkAttempts"));
    }

    [Fact]
    public async Task CiFailed_PreExistingOnBase_ParksWatch_NoStrike()
    {
        // The 2026-07-25 token-burn lesson: a check that is ALSO red
        // on the base branch head is infra breakage, not the PR's
        // fault — park the watch without consuming a rework strike.
        var gh = new FakeGitHub { Ci = CommitState.Failure, BaseCi = CommitState.Failure };
        var (task, watch) = await SeedAsync();

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, outcome);
        var taskAfter = (await _issues.GetAsync(task.Id))!;
        Assert.Null(taskAfter.GetMetadata("reworkAttempts"));       // NO strike
        Assert.Null(taskAfter.GetMetadata("reworkReason"));
        // Park record on the task via the machine (Phase 3).
        Assert.Equal("ParkedInfra", taskAfter.GetMetadata("state"));
        Assert.Equal("abc123", taskAfter.GetMetadata("parkedForSha"));
    }

    [Fact]
    public async Task CiFailed_BaseGreen_Strikes_WithFailingCheckDetails()
    {
        // Genuine PR failure: strike fires AND the rework context
        // carries the failing check names so the agent doesn't guess.
        var gh = new FakeGitHub
        {
            Ci = CommitState.Failure,
            BaseCi = CommitState.Success,
            FailedChecks = new[] { "build + test + e2e harness: Failure — e2e: no PRs were opened" },
        };
        var (task, watch) = await SeedAsync();

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        var taskAfter = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("1", taskAfter.GetMetadata("reworkAttempts"));
        Assert.Contains("build + test + e2e harness", taskAfter.GetMetadata("reworkContext"));
        Assert.Contains("no PRs were opened", taskAfter.GetMetadata("reworkContext"));
    }

    [Fact]
    public async Task ParkedWatch_BaseStillRed_StaysParked()
    {
        var gh = new FakeGitHub { Ci = CommitState.Failure, BaseCi = CommitState.Failure };
        var (task, watch) = await SeedAsync(
            taskMeta: new Dictionary<string, object>
            {
                ["state"] = "ParkedInfra",
                ["parkedForSha"] = "abc123",
            });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, outcome);
        Assert.Null((await _issues.GetAsync(task.Id))!.GetMetadata("reworkAttempts"));
        Assert.Equal("ParkedInfra", (await _issues.GetAsync(task.Id))!.GetMetadata("state"));
    }

    [Fact]
    public async Task ParkedWatch_BaseRecovered_FiresNoStrikeRefreshRound()
    {
        // Base is green again: the parked PR needs a fresh head to
        // retrigger CI — fire ONE refresh round WITHOUT consuming
        // breaker budget; the machine record moves to ReworkQueued.
        var gh = new FakeGitHub { Ci = CommitState.Failure, BaseCi = CommitState.Success };
        var (task, watch) = await SeedAsync(
            taskMeta: new Dictionary<string, object>
            {
                ["reworkAttempts"] = "2",
                ["state"] = "ParkedInfra",
                ["parkedForSha"] = "abc123",
            });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        var taskAfter = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Pending, taskAfter.Status);
        Assert.Equal("2", taskAfter.GetMetadata("reworkAttempts"));  // NOT incremented
        Assert.Contains("recovered", taskAfter.GetMetadata("reworkReason"));
        Assert.Contains("retrigger", taskAfter.GetMetadata("reworkContext"));
        Assert.Equal("ReworkQueued", taskAfter.GetMetadata("state"));
        Assert.Equal("abc123", taskAfter.GetMetadata("reworkForSha"));
    }

    [Fact]
    public async Task StalledRound_TaskInProgressUntouched_RefiresAsAnotherStrike()
    {
        // Observed live 2026-07-25 (task-160/161): a consumed rework
        // round whose run no-ops (NO_CHANGES_NEEDED) or dies
        // mid-round (restart/timeout) never moves the PR head, so
        // reworkInFlightSha == head forever and the watch stalled —
        // breaker never incremented, sprint could never complete.
        // With the round stale past the grace window, the watcher
        // must re-fire (another strike) instead of waiting forever.
        var gh = new FakeGitHub { Ci = CommitState.Failure };
        var (task, watch) = await SeedAsync();
        var watcher = new PRWatcher(gh,
            worktrees: new AgentTools.GitWorktreeService(
                new Configuration.WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
                NullLogger<AgentTools.GitWorktreeService>.Instance),
            issues: _issues,
            pollInterval: TimeSpan.FromSeconds(1),
            staleAfter: TimeSpan.FromHours(1),
            events: _events,
            logger: NullLogger<PRWatcher>.Instance,
            reworkRoundGrace: TimeSpan.Zero);   // every consumed round is instantly "stale"

        // Round 1 fires normally.
        await watcher.PollWatchOnceAsync(watch, CancellationToken.None,
            reviewsOverride: _ => Array.Empty<PullRequestReviewState>(), headShaOverride: _ => "abc123");
        Assert.Equal("1", (await _issues.GetAsync(task.Id))!.GetMetadata("reworkAttempts"));

        // Simulate the round being claimed and then dying without a
        // push (task InProgress, head unmoved).
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null, ct: CancellationToken.None);
        var watchAfter = (await _issues.GetAsync(watch.Id))!;
        var second = await watcher.PollWatchOnceAsync(watchAfter, CancellationToken.None,
            reviewsOverride: _ => Array.Empty<PullRequestReviewState>(), headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, second);
        Assert.Equal("2", (await _issues.GetAsync(task.Id))!.GetMetadata("reworkAttempts"));
    }

    [Fact]
    public async Task ConsumedRound_TaskInProgressRecentlyTouched_NoRefire()
    {
        // A legitimately-running rework round (task claimed, head not
        // yet moved, still inside the grace window) must NOT be
        // re-fired — that would double-strike a healthy round.
        var gh = new FakeGitHub { Ci = CommitState.Failure };
        var (task, watch) = await SeedAsync();
        var watcher = NewWatcher(gh);   // default 35m grace

        await watcher.PollWatchOnceAsync(watch, CancellationToken.None,
            reviewsOverride: _ => Array.Empty<PullRequestReviewState>(), headShaOverride: _ => "abc123");
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null, ct: CancellationToken.None);
        var watchAfter = (await _issues.GetAsync(watch.Id))!;
        var second = await watcher.PollWatchOnceAsync(watchAfter, CancellationToken.None,
            reviewsOverride: _ => Array.Empty<PullRequestReviewState>(), headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, second);
        Assert.Equal("1", (await _issues.GetAsync(task.Id))!.GetMetadata("reworkAttempts"));
    }

    [Fact]
    public async Task CircuitBreaker_FourthFailure_TerminalFailed()
    {
        var gh = new FakeGitHub { Ci = CommitState.Failure };
        var (task, watch) = await SeedAsync(
            taskMeta: new Dictionary<string, object> { ["reworkAttempts"] = PRWatcher.MaxReworkAttempts.ToString() });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.CiFailed, outcome);
        Assert.Equal(IssueStatus.Failed, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(IssueStatus.Failed, (await _issues.GetAsync(watch.Id))!.Status);
    }

    [Fact]
    public async Task ChangesRequested_AgentVerdict_RequeuesWithNotes()
    {
        var gh = new FakeGitHub { Ci = CommitState.Success };
        var (task, watch) = await SeedAsync(watchMeta: new Dictionary<string, object>
        {
            ["reviewSha"] = "abc123",
            // The ReviewerDispatcher records nameof(ReviewerVerdict.*)
            // — seed with the exact production value (a literal
            // "ChangesRequested" here once masked a production
            // mismatch where the watcher never saw the verdict).
            ["reviewVerdict"] = "RequestChanges",
            ["reviewNotes"] = "MetaEndpoints.cs: use OfType<string> instead of the null-forgiving Select",
        });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        var taskAfter = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Pending, taskAfter.Status);
        Assert.Contains("OfType<string>", taskAfter.GetMetadata("reworkContext"));
    }

    [Fact]
    public async Task StaleAgentVerdict_OldSha_Ignored()
    {
        // Verdict recorded for an OLD head; the agent pushed since.
        // CI green + no current verdict => keep polling, no rework.
        var gh = new FakeGitHub { Ci = CommitState.Success };
        var (_, watch) = await SeedAsync(watchMeta: new Dictionary<string, object>
        {
            ["reviewSha"] = "oldsha",
            ["reviewVerdict"] = "RequestChanges",
        });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "newsha");

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, outcome);
    }

    [Fact]
    public async Task ApprovedAgentVerdict_GreenCi_Merges()
    {
        var gh = new FakeGitHub { Ci = CommitState.Success };
        var (task, watch) = await SeedAsync(watchMeta: new Dictionary<string, object>
        {
            ["reviewSha"] = "abc123",
            ["reviewVerdict"] = "Approve",
        });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Merged, outcome);
        Assert.Equal(1, gh.MergeCalls);
        Assert.Equal(IssueStatus.Completed, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(IssueStatus.Completed, (await _issues.GetAsync(watch.Id))!.Status);
    }

    [Fact]
    public async Task ReviewerError_ThirdRound_BlocksForOperator()
    {
        var gh = new FakeGitHub { Ci = CommitState.Success };
        var (task, watch) = await SeedAsync(watchMeta: new Dictionary<string, object>
        {
            ["reviewSha"] = "abc123",
            ["reviewVerdict"] = "Error",
            ["reviewRound"] = PRWatcher.MaxReworkAttempts.ToString(),
        });

        var outcome = await NewWatcher(gh).PollWatchOnceAsync(
            watch, CancellationToken.None, reviewsOverride: _ => Array.Empty<PullRequestReviewState>(),
            headShaOverride: _ => "abc123");

        Assert.Equal(PRWatcher.WatchPollOutcome.Blocked, outcome);
        Assert.Equal(IssueStatus.Blocked, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(IssueStatus.Blocked, (await _issues.GetAsync(watch.Id))!.Status);
    }
}
