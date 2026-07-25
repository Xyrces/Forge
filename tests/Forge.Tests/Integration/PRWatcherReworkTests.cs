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
        public bool MergeResult = true;
        public int MergeCalls;
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult(new PullRequest(number));
        public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(Ci);
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
        gates: gates);

    private async Task<(IssueRecord task, IssueRecord watch)> SeedAsync(
        Dictionary<string, object>? taskMeta = null,
        Dictionary<string, object>? watchMeta = null)
    {
        var task = await _issues.CreateAsync(new Forge.Core.NewIssue(
            Type: "task", Title: "implement X", Metadata: taskMeta));
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
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(task.Id))!.Status);

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
        Assert.Equal("abc123", watchAfter.GetMetadata("reworkInFlightSha"));
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
