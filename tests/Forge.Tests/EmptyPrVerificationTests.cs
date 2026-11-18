using System.Diagnostics;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Workflow;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Forge.Tests;

public sealed class EmptyPrVerificationTests : IDisposable
{
    private readonly string _root = TempRoot.Instance.NewDirectory("empty-pr-verification");
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("empty-pr-verification"), "project");
    private readonly FakeGitHub _github = new();
    private readonly GitWorktreeService _worktrees;
    public EmptyPrVerificationTests()
    {
        Directory.CreateDirectory(_root);
        _worktrees = new(new WorkspaceOptions { Root = _root, DefaultBranch = "main", WorktreeRoot = ".worktrees" }, NullLogger<GitWorktreeService>.Instance);
    }

    private async Task<IssueRecord> Seed(SprintEvidenceKind kind = SprintEvidenceKind.ImplementationVerified, bool blocked = false)
    {
        await Format42Fixture.UpgradeAsync(_issues);
        var scope = await ApprovedSprintFixture.Scope(_issues);
        var task = await ApprovedSprintFixture.Task(_issues);
        await new SprintOutcomeAuthority(_issues).BindAsync(task.Id, new(1, "accepted", "Existing accepted behavior",
            [new("code", "Approved behavior works", kind, scope)], [task.Id], ["code"], SprintMemberReason.Delivers, scope));
        var sprints = new SprintStore(_issues);
        var snapshot = await new SprintActivationValidator(_issues, sprints, new SpecStore(_issues), "project", null).ValidateAsync([task.Id], default);
        await sprints.CreateWithIssuesAsync(new("Outcome", "Existing accepted behavior", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        await _issues.ClaimAsync(task.Id, "coredev");
        await _issues.SetCheckpointAsync(task.Id, DispatchCheckpoint.PrOpened);
        return await _issues.TransitionAsync(task.Id, blocked ? IssueStatus.Blocked : IssueStatus.InProgress, null,
            new Dictionary<string, object> { ["prNumber"] = 12, ["branch"] = "agent/task", ["state"] = blocked ? "BlockedOperator" : "PROpen",
                ["reviewSha"] = _github.Head, ["reviewVerdict"] = "Approve", ["qaVerdict"] = "pass", ["reworkAttempts"] = 2 });
    }
    private PRWatcher Watch(Func<string, CancellationToken, Task<bool>>? joined = null,
        Func<string, CancellationToken, Task<bool>>? durable = null, AgentRunStore? runs = null) => new(_github, _worktrees, _issues,
        TimeSpan.FromSeconds(1), TimeSpan.FromHours(24), new InMemoryDashboardEventBus(), NullLogger<PRWatcher>.Instance,
        lifecycle: new(true, NullLogger.Instance), joinTaskWorkers: joined ?? ((_, _) => Task.FromResult(true)),
        hasDurableWorker: durable, runs: runs, projectId: "project");

    [Theory]
    [InlineData(SprintEvidenceKind.MergedAndVerified, "durable", false)]
    [InlineData(SprintEvidenceKind.MergedAndVerified, "active", false)]
    [InlineData(SprintEvidenceKind.MergedAndVerified, "checkpoint", false)]
    [InlineData(SprintEvidenceKind.ImplementationVerified, "durable", false)]
    [InlineData(SprintEvidenceKind.ImplementationVerified, "active", false)]
    [InlineData(SprintEvidenceKind.ImplementationVerified, "checkpoint", false)]
    [InlineData(SprintEvidenceKind.MergedAndVerified, "durable", true)]
    [InlineData(SprintEvidenceKind.MergedAndVerified, "active", true)]
    [InlineData(SprintEvidenceKind.MergedAndVerified, "checkpoint", true)]
    [InlineData(SprintEvidenceKind.ImplementationVerified, "durable", true)]
    [InlineData(SprintEvidenceKind.ImplementationVerified, "active", true)]
    [InlineData(SprintEvidenceKind.ImplementationVerified, "checkpoint", true)]
    public async Task RightfulWorkerDefersBeforeScopeDecisionOrJoining(SprintEvidenceKind kind, string ownership, bool sameBranchTask)
    {
        var task = await Seed(kind);
        var owner = task;
        if (sameBranchTask)
        {
            owner = await _issues.CreateAsync(new("coredev", "Same branch repair", Metadata: new Dictionary<string, object> { ["branch"] = "agent/task" }));
            owner = (await _issues.ClaimAsync(owner.Id, "coredev"))!;
            await _issues.SetCheckpointAsync(owner.Id, DispatchCheckpoint.PrOpened);
        }
        if (ownership == "checkpoint") await _issues.SetCheckpointAsync(owner.Id, DispatchCheckpoint.WorktreeAcquired);
        var runs = new AgentRunStore(_issues.Db);
        if (ownership == "active") await runs.StartAsync("repair-run", owner.Id, "CoreDev", "fixture", projectId: "project");
        task = (await _issues.GetAsync(task.Id))!;
        var joinCalls = 0;
        var watcher = Watch((_, _) => { joinCalls++; return Task.FromResult(true); },
            (id, _) => Task.FromResult(ownership == "durable" && id == owner.Id), runs);
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await watcher.PollWatchedTaskAsync(task));
        Assert.Equal(task.MetadataJson, (await _issues.GetAsync(task.Id))!.MetadataJson);
        Assert.Equal(task.Status, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(0, joinCalls); Assert.Equal(0, _github.CloseCalls); Assert.Equal(0, _github.DeleteCalls);
    }

    [Theory]
    [InlineData(SprintEvidenceKind.MergedAndVerified)]
    [InlineData(SprintEvidenceKind.ImplementationVerified)]
    public async Task BlockedResumeDoesNotRetireOrJoinRightfulWorker(SprintEvidenceKind kind)
    {
        var task = await Seed(kind, blocked: true);
        var joined = false;
        Assert.Null(await Watch((_, _) => { joined = true; return Task.FromResult(true); },
            (_, _) => Task.FromResult(true)).TryResumeMergeableBlockedAsync(task));
        Assert.False(joined); Assert.Equal(0, _github.CloseCalls);
        Assert.Equal(task.MetadataJson, (await _issues.GetAsync(task.Id))!.MetadataJson);
    }

    [Fact]
    public async Task WorkerObservedAfterHistoricalJoinStillPreventsScopeBlock()
    {
        var task = await Seed(SprintEvidenceKind.MergedAndVerified);
        var active = false;
        var watcher = Watch((_, _) => { active = true; return Task.FromResult(true); }, (_, _) => Task.FromResult(active));
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await watcher.PollWatchedTaskAsync(task));
        Assert.Equal(task.MetadataJson, (await _issues.GetAsync(task.Id))!.MetadataJson);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task UnrelatedBranchWorkerDoesNotHoldEmptyPrVerification()
    {
        var task = await Seed();
        var unrelated = await _issues.CreateAsync(new("coredev", "Other branch", Metadata: new Dictionary<string, object> { ["branch"] = "agent/unrelated" }));
        await _issues.ClaimAsync(unrelated.Id, "coredev");
        var runs = new AgentRunStore(_issues.Db);
        await runs.StartAsync("unrelated-run", unrelated.Id, "CoreDev", "fixture", projectId: "project");
        var watcher = Watch(durable: (id, _) => Task.FromResult(id == unrelated.Id), runs: runs);
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await watcher.PollWatchedTaskAsync(task));
        Assert.Equal(1, _github.CloseCalls);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(unrelated.Id))!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EmptyPrQueuesFreshVerificationWithoutDeliveryAndSurvivesMissedCallback(bool blocked)
    {
        var task = await Seed(blocked: blocked);
        if (blocked) Assert.Null(await Watch().TryResumeMergeableBlockedAsync(task));
        else Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch().PollWatchedTaskAsync(task));
        await using var reopened = new IssueStore(_issues.Db, "project");
        var pending = (await reopened.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Pending, pending.Status);
        Assert.Equal("Pending", pending.GetMetadata("state"));
        Assert.Equal("EmptyPrRetired", pending.GetMetadata("lastEvent"));
        Assert.Null(pending.GetMetadata("prNumber")); Assert.Null(pending.GetMetadata("reviewVerdict")); Assert.Null(pending.GetMetadata("qaVerdict"));
        Assert.Equal(task.GetMetadata(SprintPlanJson.MetadataKey), pending.GetMetadata(SprintPlanJson.MetadataKey));
        Assert.Contains(await reopened.ListEventsAsync(task.Id), e => e.Kind == "empty_pr_retired");
        Assert.Contains(await reopened.ReadyAsync(10), i => i.Id == task.Id);
        Assert.Equal(1, _github.CloseCalls); Assert.Equal(0, _github.DeleteCalls);
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task));
        Assert.Equal(1, _github.CloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WorkerStillActiveOrUnconfirmedNeverClosesOrRetires(bool throws)
    {
        var task = await Seed();
        var watcher = Watch((_, _) => throws ? throw new InvalidOperationException("Pod join unavailable") : Task.FromResult(false));
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await watcher.PollWatchedTaskAsync(task));
        Assert.Equal(task.MetadataJson, (await _issues.GetAsync(task.Id))!.MetadataJson);
        Assert.Equal(0, _github.CloseCalls);
    }

    [Fact]
    public async Task StrictMergedScopeCannotBeReinterpretedFromEmptyDiff()
    {
        var task = await Seed(SprintEvidenceKind.MergedAndVerified);
        Assert.Equal(PRWatcher.WatchPollOutcome.Blocked, await Watch().PollWatchedTaskAsync(task));
        Assert.Equal(IssueStatus.Blocked, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal(0, _github.CloseCalls);
    }

    [Theory]
    [InlineData(CloseDispositionKind.DeliveredElsewhere)] [InlineData(CloseDispositionKind.DuplicateDelivered)]
    public async Task PositiveProseClosureCannotTerminalizeCommittedMember(CloseDispositionKind kind)
    {
        var task = await Seed();
        var other = await _issues.CreateAsync(new("task", "Other implementation"));
        await _issues.TransitionAsync(other.Id, IssueStatus.Completed, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _issues.CloseWithDispositionAsync(task.Id, new(kind, "Already done", "model prose", other.Id)));
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task ClosedPrAfterLostAcknowledgementIsReconciledWithoutAnotherClose()
    {
        var task = await Seed();
        _github.LoseCloseAck = true;
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task));
        Assert.NotNull((await _issues.GetAsync(task.Id))!.GetMetadata("prNumber"));
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch().PollWatchedTaskAsync(task));
        Assert.Equal(1, _github.CloseCalls);
    }

    [Fact]
    public async Task RetirementAuditFailureRollsBackTaskAndRetryUsesSameClosedPr()
    {
        var task = await Seed();
        await using var connection = await _issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_retirement BEFORE INSERT ON issue_event WHEN NEW.kind='empty_pr_retired' BEGIN SELECT RAISE(ABORT,'fixture failure'); END;";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task));
        Assert.Equal(task.MetadataJson, (await _issues.GetAsync(task.Id))!.MetadataJson);
        command.CommandText = "DROP TRIGGER reject_retirement"; await command.ExecuteNonQueryAsync();
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch().PollWatchedTaskAsync(task));
        Assert.Equal(1, _github.CloseCalls);
    }

    [Fact]
    public async Task BusyQaDoesNotBlockWatchLoopOrClosePr()
    {
        var task = await Seed();
        var gate = ReviewWorkerAttempt.Gate("project", task.Id, AgentType.QA);
        await gate.WaitAsync();
        try
        {
            Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, _github.CloseCalls);
        }
        finally { gate.Release(); }
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch().PollWatchedTaskAsync(task));
    }

    [Fact]
    public async Task OverlappingPollAndLateCallbackCannotActOnRetiredPr()
    {
        var task = await Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Watch(async (_, _) => { entered.SetResult(); await release.Task; return true; }).PollWatchedTaskAsync(task);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task));
        release.SetResult();
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await first);
        var calls = _github.ReadCalls;
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task));
        Assert.Equal(calls, _github.ReadCalls);
        Assert.Equal(1, _github.CloseCalls);
    }

    [Fact]
    public async Task ConcurrentTaskChangeCannotRetireUsingStaleSnapshot()
    {
        var task = await Seed();
        _github.OnClose = () => _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "operator hold");
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch().PollWatchedTaskAsync(task));
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Blocked, after.Status);
        Assert.Equal("12", after.GetMetadata("prNumber"));
        Assert.Null(after.GetMetadata("existingVerificationRequest"));
    }

    [Fact]
    public async Task NewDispatchUsesFreshConfiguredSourceAndPreservesOldCheckout()
    {
        Git("init", "-q", "-b", "main"); Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "code.txt"), "existing behavior");
        Git("add", "."); Git("commit", "-qm", "existing"); Git("remote", "add", "origin", _root);
        _github.Head = _github.Base = Git("rev-parse", "HEAD").Trim();
        var task = await Seed();
        var oldPath = await _worktrees.CreateFreshAsync(task.Id, "main", branchOverride: "agent/task");
        await Watch().PollWatchedTaskAsync(task);
        var pending = (await _issues.GetAsync(task.Id))!;
        var claimed = (await _issues.ClaimAsync(task.Id, "coredev"))!;
        var result = await WorktreeExecutor.HandleAsync(new(claimed, ClaimResult.Ok, _root, pending.GetMetadata("branch")), _issues,
            _worktrees, "main", NullLogger.Instance, default);
        Assert.NotEqual(oldPath, result.WorktreePath);
        Assert.True(Directory.Exists(oldPath));
        Assert.Equal(_github.Base, await _worktrees.GetHeadShaAsync(result.WorktreePath!, default));
        Assert.Equal(pending.GetMetadata("branch"), GitAt(result.WorktreePath!, "branch", "--show-current").Trim());
    }

    private sealed class FakeGitHub() : GitHubService("owner", "repo", null)
    {
        public override bool RequireFormalReview => false;
        public string Head = new('a', 40), Base = new('b', 40);
        public bool Closed, LoseCloseAck;
        public int CloseCalls, DeleteCalls, ReadCalls;
        public Func<Task>? OnClose;
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            var repo = new Repository(); Set(repo, "FullName", "owner/repo");
            var head = new GitReference(null!, null!, null!, "agent/task", Head, null!, repo);
            var basis = new GitReference(null!, null!, null!, "main", Base, null!, repo);
            var pr = new PullRequest(number);
            Set(pr, "Head", head); Set(pr, "Base", basis); Set(pr, "ChangedFiles", 0);
            Set(pr, "State", new StringEnum<ItemState>(Closed ? ItemState.Closed : ItemState.Open));
            return Task.FromResult(pr);
        }
        public override async Task<PullRequest> ClosePullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            CloseCalls++; Closed = true;
            if (LoseCloseAck) { LoseCloseAck = false; throw new IOException("Lost response after GitHub closed PR"); }
            if (OnClose is not null) await OnClose();
            return await GetPullRequestAsync(number, cancellationToken);
        }
        public override Task<bool> DeleteBranchAsync(string branch, CancellationToken cancellationToken = default)
        { DeleteCalls++; return Task.FromResult(true); }
        private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    }
    private string Git(params string[] args) => GitAt(_root, args);
    private static string GitAt(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.True(process.ExitCode == 0, error); return output;
    }
    public void Dispose() => _issues.Dispose();
}
