using Forge.Core;
using Forge.Dashboard;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// The watch-lane QA gate in PRWatcher (project $qa flag): the merge
/// gate requires qaVerdict=pass at the CURRENT head; QA-pending keeps
/// the gate closed without striking; a fail verdict at the head
/// requeues a rework round with the QA notes as context.
/// </summary>
public class PRWatcherQaGateTests : IDisposable
{
    private const string Head = "abc123qa";

    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly InMemoryDashboardEventBus _events;

    public PRWatcherQaGateTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("qa-gate");
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

    private sealed class FakeGitHub : GitHubService
    {
        public CommitState Ci = CommitState.Success;
        public int MergeCalls;
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            var pr = new PullRequest(number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.ChangedFiles))!
                .SetValue(pr, 1);
            return Task.FromResult(pr);
        }
        public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(Ci);
        public override Task<int> GetCiSignalCountAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(1);
        public override Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default)
            => Task.FromResult("main-head-sha");
        public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<PullRequestReview>)new List<PullRequestReview>());
        public override Task<bool> MergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            MergeCalls++;
            return Task.FromResult(true);
        }
    }

    private PRWatcher NewWatcher(FakeGitHub gh, bool qaEnabled) => new(
        gh,
        worktrees: new AgentTools.GitWorktreeService(
            new Configuration.WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<AgentTools.GitWorktreeService>.Instance),
        issues: _issues,
        pollInterval: TimeSpan.FromSeconds(1),
        staleAfter: TimeSpan.FromHours(1),
        events: _events,
        logger: NullLogger<PRWatcher>.Instance,
        lifecycle: new Forge.Core.TaskStateMachine(writeAuthority: false, NullLogger.Instance),
        qaEnabled: qaEnabled);

    private async Task<IssueRecord> SeedAsync(Dictionary<string, object>? meta = null)
    {
        var tm = meta ?? new Dictionary<string, object>();
        tm["prNumber"] = "42";
        // Reviewer-agent approval at the current head — the merge gate
        // then hinges on the QA verdict alone.
        tm["reviewSha"] = Head;
        tm["reviewVerdict"] = nameof(ReviewerVerdict.Approve);
        var task = await _issues.CreateAsync(new Forge.Core.NewIssue(
            "task", "implement X", Metadata: tm));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null);
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object> { ["branch"] = $"agent/{task.Id}" });
        return (await _issues.GetAsync(task.Id))!;
    }

    private Task<PRWatcher.WatchPollOutcome> Poll(PRWatcher watcher, IssueRecord task)
        => watcher.PollWatchedTaskAsync(task, CancellationToken.None, headShaOverride: _ => Head);

    [Fact]
    public async Task QaEnabled_QaPending_NoMerge_NoStrike()
    {
        var gh = new FakeGitHub();
        var task = await SeedAsync();
        var watcher = NewWatcher(gh, qaEnabled: true);

        var outcome = await Poll(watcher, task);

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, outcome);
        Assert.Equal(0, gh.MergeCalls);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.InProgress, after.Status);
        Assert.Null(after.GetMetadata("reworkReason"));
    }

    [Fact]
    public async Task QaEnabled_PassAtHead_Merges()
    {
        var gh = new FakeGitHub();
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["qaSha"] = Head,
            ["qaVerdict"] = QaDispatcher.VerdictPass,
        });
        var watcher = NewWatcher(gh, qaEnabled: true);

        var outcome = await Poll(watcher, task);

        Assert.Equal(PRWatcher.WatchPollOutcome.Merged, outcome);
        Assert.Equal(1, gh.MergeCalls);
    }

    [Fact]
    public async Task QaEnabled_StaleVerdict_NoMerge()
    {
        var gh = new FakeGitHub();
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["qaSha"] = "older-head-sha",
            ["qaVerdict"] = QaDispatcher.VerdictPass,
        });
        var watcher = NewWatcher(gh, qaEnabled: true);

        var outcome = await Poll(watcher, task);

        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, outcome);
        Assert.Equal(0, gh.MergeCalls);
    }

    [Fact]
    public async Task QaEnabled_FailAtHead_ReworksWithQaNotes()
    {
        var gh = new FakeGitHub();
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["qaSha"] = Head,
            ["qaVerdict"] = QaDispatcher.VerdictFail,
            ["qaNotes"] = "menu button does nothing",
        });
        var watcher = NewWatcher(gh, qaEnabled: true);

        var outcome = await Poll(watcher, task);

        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, outcome);
        Assert.Equal(0, gh.MergeCalls);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("QA playthrough failed", after.GetMetadata("reworkReason"));
        Assert.Contains("menu button", after.GetMetadata("reworkContext"));
    }

    [Fact]
    public async Task QaDisabled_FailVerdictIgnored_Merges()
    {
        var gh = new FakeGitHub();
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["qaSha"] = Head,
            ["qaVerdict"] = QaDispatcher.VerdictFail,
        });
        var watcher = NewWatcher(gh, qaEnabled: false);

        var outcome = await Poll(watcher, task);

        Assert.Equal(PRWatcher.WatchPollOutcome.Merged, outcome);
        Assert.Equal(1, gh.MergeCalls);
    }
}
