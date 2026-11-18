using Forge.Core;
using Forge.Dashboard;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using NewIssue = Forge.Core.NewIssue;
using Xunit;

namespace Forge.Tests.Integration;

public sealed class FormalReviewGateTests : IDisposable
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<SemaphoreSlim> UnownedGate(string task) => new(PRWatcher.GetPollGate("gate-lifetime", task));

    [Fact]
    public async Task GateCacheDoesNotRetainInactiveSemaphoresOrReplaceAnOwnedGate()
    {
        var weak = UnownedGate(Guid.NewGuid().ToString("N"));
        var held = PRWatcher.GetPollGate("gate-lifetime", "held");
        await held.WaitAsync();
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(weak.TryGetTarget(out _));
            Assert.Same(held, PRWatcher.GetPollGate("gate-lifetime", "held"));
        }
        finally { held.Release(); }
    }

    [Fact]
    public void IndependentTasksNeverSharePollGatesAndPolicyLocksHaveStableOrder()
    {
        var ids = Enumerable.Range(0, 256).Select(i => "task-" + i).ToArray();
        var forward = PRWatcher.GetPollGatesForTasks("isolated-gates", ids);
        var reverse = PRWatcher.GetPollGatesForTasks("isolated-gates", ids.Reverse());
        Assert.Equal(256, forward.Count);
        Assert.Equal(forward, reverse);
        Assert.NotSame(PRWatcher.GetPollGate("project-a", "task-1"), PRWatcher.GetPollGate("project-b", "task-1"));
    }

    private readonly string _root = TempRoot.Instance.NewDirectory("formal-review");
    private readonly IssueStore _issues;
    public FormalReviewGateTests() => _issues = new IssueStore(Path.Combine(_root, "state.db"));
    public void Dispose() => _issues.Dispose();

    private sealed class GitHub : GitHubService
    {
        public GitHub() : base("owner", "repo") { }
        public override string? ReviewerLogin => "reviewer[bot]";
        public int Reads, Merges;
        public ProtectionReviewDecision Protection = ProtectionReviewDecision.NotRequired;
        public bool ProtectionUnavailable;
        public Action? OnMerge;
        public override Task<ProtectionReviewDecision> GetProtectionReviewDecisionAsync(int number, string head, CancellationToken ct = default)
            => ProtectionUnavailable ? Task.FromException<ProtectionReviewDecision>(new InvalidOperationException("read unavailable")) : Task.FromResult(Protection);
        public bool Label;
        public CommitState Ci = CommitState.Success;
        public bool Mergeable = true;
        public Func<int, IReadOnlyList<FormalDecision>> Decisions = _ => [Decision("reviewer[bot]", true, PullRequestReviewState.Approved)];
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            var pr = new PullRequest(number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))!.SetValue(pr, new GitReference(null!, null!, null!, "branch", "head", null!, null!));
            typeof(PullRequest).GetProperty(nameof(PullRequest.ChangedFiles))!.SetValue(pr, 1);
            typeof(PullRequest).GetProperty(nameof(PullRequest.State))!.SetValue(pr, new StringEnum<ItemState>(ItemState.Open));
            typeof(PullRequest).GetProperty(nameof(PullRequest.Mergeable))!.SetValue(pr, Mergeable);
            return Task.FromResult(pr);
        }
        public override Task<bool> HasHumanReviewLabelAsync(int number, CancellationToken ct = default) => Task.FromResult(Label);
        public override Task SetHumanReviewLabelAsync(int number, bool required, CancellationToken ct = default) { Label = required; return Task.CompletedTask; }
        public override Task<string> GetReviewReworkContextAsync(int number, IReadOnlyList<FormalDecision> requests, CancellationToken ct = default) => Task.FromResult("original human finding");
        public override Task<IReadOnlyList<FormalDecision>> GetAuthorizedDecisionsAsync(int number, string? author, CancellationToken ct = default) => Task.FromResult(Decisions(++Reads));
        public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default) => Task.FromResult(sha == "base" ? CommitState.Success : Ci);
        public override Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default) => Task.FromResult("base");
        public override Task<IReadOnlyList<string>> GetFailedCheckRunSummariesAsync(string sha, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["test failure"]);
        public override Task<bool> MergePullRequestAsync(int number, CancellationToken cancellationToken = default, string? expectedHeadSha = null) { Merges++; OnMerge?.Invoke(); return Task.FromResult(false); }
        public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PullRequestReview>>([]);
    }
    private static FormalDecision Decision(string user, bool bot, PullRequestReviewState state, string head = "head", long id = 1) => new(user, head, state, bot, id, "finding", "url");
    private PRWatcher Watch(GitHub github) => new(github,
        new AgentTools.GitWorktreeService(new Configuration.WorkspaceOptions { Root = _root, WorktreeRoot = ".wt", DefaultBranch = "main" }, NullLogger<AgentTools.GitWorktreeService>.Instance),
        _issues, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), new InMemoryDashboardEventBus(), NullLogger<PRWatcher>.Instance);
    private async Task<IssueRecord> Seed()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = "42", ["branch"] = "branch", ["reviewVerdict"] = "Approve", ["reviewSha"] = "head" }));
        return await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NominatedHumanAppliesToProjectPolicyAndAdditionalHold(bool taskHold)
    {
        var task = await Seed();
        var policy = new PullRequestReviewPolicy(new MemoryStore(_issues.Db));
        await policy.SetAsync(taskHold ? RequiredReview.Automated : RequiredReview.Both,"Owner","authenticated-operator",default);
        if (taskHold) await policy.SetHoldAsync(task.Id,new(true,true),default);
        var github = new GitHub { Decisions = _ => [Decision("reviewer[bot]",true,PullRequestReviewState.Approved),Decision("another-writer",false,PullRequestReviewState.Approved)] };
        await Watch(github).PollWatchedTaskAsync(task);
        Assert.Equal(0,github.Merges);
        Assert.Equal("True",(await _issues.GetAsync(task.Id))!.GetMetadata("awaitingHumanReview"));
        github.Decisions = _ => [Decision("reviewer[bot]",true,PullRequestReviewState.Approved),Decision("OWNER",false,PullRequestReviewState.Approved)];
        await Watch(github).PollWatchedTaskAsync((await _issues.GetAsync(task.Id))!);
        Assert.Equal(1,github.Merges);
    }

    [Fact]
    public async Task AppApprovalWaitsForEligibleProtectedApprovalWithoutMergeRetries()
    {
        var task = await Seed();
        var github = new GitHub { Protection = ProtectionReviewDecision.ReviewRequired };
        for (var i = 0; i < 3; i++)
            Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch(github).PollWatchedTaskAsync((await _issues.GetAsync(task.Id))!));
        var waiting = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(0, github.Merges);
        Assert.Null(waiting.GetMetadata("reworkAttempts"));
        Assert.Equal("True", waiting.GetMetadata("awaitingHumanReview"));
        Assert.Equal("GitHub approval required", TaskSituation.Describe(waiting).Label);
        github.Decisions = _ => [Decision("reviewer[bot]", true, PullRequestReviewState.Approved), Decision("human", false, PullRequestReviewState.Approved)];
        github.Protection = ProtectionReviewDecision.Approved;
        await Watch(github).PollWatchedTaskAsync(waiting);
        Assert.Equal(1, github.Merges);
        Assert.Equal("False", (await _issues.GetAsync(task.Id))!.GetMetadata("awaitingHumanReview"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProtectedApprovalWaitAndUnavailableReadStillAllowCiRepair(bool unavailable)
    {
        var github = new GitHub { Protection = ProtectionReviewDecision.ReviewRequired, ProtectionUnavailable = unavailable, Ci = CommitState.Failure };
        var task = await Seed();
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal(0, github.Merges);
        Assert.Equal("False", (await _issues.GetAsync(task.Id))!.GetMetadata("awaitingHumanReview"));
    }

    [Fact]
    public async Task UnavailableProtectionFailsClosedWithoutInventingHumanWait()
    {
        var github = new GitHub { ProtectionUnavailable = true };
        var task = await Seed();
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal(0, github.Merges);
        var waiting = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("False", waiting.GetMetadata("awaitingHumanReview"));
        Assert.Equal("Review check unavailable", TaskSituation.Describe(waiting).Label);
    }

    [Fact]
    public async Task ProtectedRequestedChangesStillQueueCurrentHeadCorrection()
    {
        var task = await Seed();
        var github = new GitHub { Protection = ProtectionReviewDecision.ChangesRequested,
            Decisions = _ => [Decision("human", false, PullRequestReviewState.ChangesRequested)] };
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal(0, github.Merges);
        Assert.Equal("False", (await _issues.GetAsync(task.Id))!.GetMetadata("awaitingHumanReview"));
    }

    [Theory]
    [InlineData(ProtectionReviewDecision.NotRequired)]
    [InlineData(ProtectionReviewDecision.Approved)]
    public async Task GithubDecisionNeverOverridesForgeHoldOrOldHumanObjection(ProtectionReviewDecision protection)
    {
        var task = await Seed();
        await new PullRequestReviewPolicy(new MemoryStore(_issues.Db)).SetHoldAsync(task.Id, new(true, true), default);
        var github = new GitHub { Protection = protection };
        await Watch(github).PollWatchedTaskAsync(task);
        Assert.Equal(0, github.Merges);
        await new PullRequestReviewPolicy(new MemoryStore(_issues.Db)).SetHoldAsync(task.Id, new(false, true), default);
        github.Decisions = _ => [Decision("reviewer[bot]", true, PullRequestReviewState.Approved), Decision("human", false, PullRequestReviewState.ChangesRequested, "old")];
        await Watch(github).PollWatchedTaskAsync((await _issues.GetAsync(task.Id))!);
        Assert.Equal(0, github.Merges);
    }

    [Fact]
    public async Task ProtectedWaitIsNotTimedOutAndRecoveryDoesNotBypassIt()
    {
        var task = await Seed();
        task = await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object> { ["prOpenedAt"] = DateTimeOffset.UtcNow.AddDays(-3).ToString("O") });
        var github = new GitHub { Protection = ProtectionReviewDecision.ReviewRequired };
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(task.Id))!.Status);
        task = await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, null);
        Assert.Null(await Watch(github).TryResumeMergeableBlockedAsync(task));
        Assert.Equal(0, github.Merges);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProtectedWaitAndOutageRetainNamedHumanRequirement(bool unavailable)
    {
        var task = await Seed();
        await new PullRequestReviewPolicy(new MemoryStore(_issues.Db)).SetAsync(RequiredReview.Human, "Xyrces", "operator", default);
        var github = new GitHub { Protection = ProtectionReviewDecision.ReviewRequired, ProtectionUnavailable = unavailable,
            Decisions = _ => [] };
        await Watch(github).PollWatchedTaskAsync(task);
        var waiting = (await _issues.GetAsync(task.Id))!;
        var situation = TaskSituation.Describe(waiting, nominatedHuman: "Xyrces");
        Assert.Equal("@Xyrces", situation.NextActor);
        Assert.DoesNotContain("Forge reviewer approval", situation.Text);
        Assert.Equal(unavailable ? "Waiting for human review" : "GitHub approval required", situation.Label);
        Assert.Equal("True", waiting.GetMetadata("awaitingHumanReview"));
        Assert.Equal(0, github.Merges);
    }

    [Fact]
    public async Task AggregateOldRequestedChangesStayVisibleWithoutRepeatingCorrection()
    {
        var task = await Seed();
        var github = new GitHub { Protection = ProtectionReviewDecision.ChangesRequested,
            Decisions = _ => [Decision("reviewer[bot]", true, PullRequestReviewState.Approved), Decision("human", false, PullRequestReviewState.ChangesRequested, "old")] };
        await Watch(github).PollWatchedTaskAsync(task);
        var waiting = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("GitHub changes requested", TaskSituation.Describe(waiting).Label);
        Assert.Null(waiting.GetMetadata("reworkAttempts"));
        Assert.Equal(0, github.Merges);
    }

    [Fact]
    public async Task RefusedMergeImmediatelyRecordsNewProtectionWait()
    {
        var task = await Seed();
        var github = new GitHub();
        github.OnMerge = () => github.Protection = ProtectionReviewDecision.ReviewRequired;
        await Watch(github).PollWatchedTaskAsync(task);
        var waiting = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(1, github.Merges);
        Assert.Equal("True", waiting.GetMetadata("awaitingHumanReview"));
        Assert.Equal("GitHub approval required", TaskSituation.Describe(waiting).Label);
        await Watch(github).PollWatchedTaskAsync(waiting);
        Assert.Equal(1, github.Merges);
    }

    [Fact]
    public async Task ClearingNominationRestoresEligibleHumanFallbackWithoutClearingHumanMode()
    {
        var task=await Seed();var policy=new PullRequestReviewPolicy(new MemoryStore(_issues.Db));
        await policy.SetAsync(RequiredReview.Human,"Owner","authenticated-operator",default);
        var github=new GitHub { Decisions=_=>[Decision("another-writer",false,PullRequestReviewState.Approved)] };
        await Watch(github).PollWatchedTaskAsync(task);Assert.Equal(0,github.Merges);
        await policy.SetAsync(RequiredReview.Human,null,"authenticated-operator",default);
        await Watch(github).PollWatchedTaskAsync((await _issues.GetAsync(task.Id))!);Assert.Equal(1,github.Merges);
    }

    [Fact]
    public async Task HumanObjectionArrivingAfterInitialApprovalPreventsMerge()
    {
        var task = await Seed();
        var github = new GitHub { Decisions = read => read == 1
            ? [Decision("reviewer[bot]", true, PullRequestReviewState.Approved)]
            : [Decision("reviewer[bot]", true, PullRequestReviewState.Approved), Decision("human", false, PullRequestReviewState.ChangesRequested)] };
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal(0, github.Merges);
        Assert.True(github.Reads >= 2);
    }

    [Fact]
    public async Task MetadataApprovalCannotSubstituteForFormalReview()
    {
        var github = new GitHub { Decisions = _ => [] };
        await Watch(github).PollWatchedTaskAsync(await Seed());
        Assert.Equal(0, github.Merges);
    }

    [Fact]
    public async Task PrePrHoldSynchronizesLabelAndWaitsWithoutStriking()
    {
        var task = await Seed();
        var policy = new PullRequestReviewPolicy(new MemoryStore(_issues.Db));
        await policy.SetHoldAsync(task.Id, new(true, true), default);
        var github = new GitHub();
        await Watch(github).PollWatchedTaskAsync(task);
        Assert.True(github.Label);
        Assert.False((await policy.GetHoldAsync(task.Id, default)).SyncPending);
        Assert.Equal(0, github.Merges);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Equal("True", (await _issues.GetAsync(task.Id))!.GetMetadata("awaitingHumanReview"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ArmedHumanHoldDoesNotBlockAutomaticRepairOrCountAsHumanWait(bool conflict)
    {
        var task = await Seed();
        var policy = new PullRequestReviewPolicy(new MemoryStore(_issues.Db));
        await policy.SetHoldAsync(task.Id, new(true, true), default);
        var github = new GitHub { Ci = conflict ? CommitState.Success : CommitState.Failure, Mergeable = !conflict };
        Assert.Equal(PRWatcher.WatchPollOutcome.Reworking, await Watch(github).PollWatchedTaskAsync(task));
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.True((await policy.GetHoldAsync(task.Id, default)).Required);
        Assert.Equal("False", after.GetMetadata("awaitingHumanReview"));
        Assert.Null(after.GetMetadata("humanReviewWaitStartedAt"));
        Assert.Equal(0, github.Merges);
    }

    [Fact]
    public async Task HumanWaitStartsOnlyAfterAutomatedReviewFinishes()
    {
        var task = await Seed();
        await new PullRequestReviewPolicy(new MemoryStore(_issues.Db)).SetModeAsync(RequiredReview.Both, default);
        var github = new GitHub { Decisions = _ => [] };
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal("False", (await _issues.GetAsync(task.Id))!.GetMetadata("awaitingHumanReview"));
        Assert.Null((await _issues.GetAsync(task.Id))!.GetMetadata("humanReviewWaitStartedAt"));
    }

    [Fact]
    public async Task HumanOnlyCanMergeWithoutAutomatedApprovalAndRefusalRereadsDecisions()
    {
        var policy = new PullRequestReviewPolicy(new MemoryStore(_issues.Db));
        await policy.SetModeAsync(RequiredReview.Human, default);
        var github = new GitHub { Decisions = _ => [Decision("human", false, PullRequestReviewState.Approved)] };
        await Watch(github).PollWatchedTaskAsync(await Seed());
        Assert.Equal(1, github.Merges);
        Assert.True(github.Reads >= 4);
    }

    [Fact]
    public async Task RecoveryCannotUseMetadataApprovalToBypassHumanPolicy()
    {
        var task = await Seed();
        task = await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, null);
        await new PullRequestReviewPolicy(new MemoryStore(_issues.Db)).SetModeAsync(RequiredReview.Both, default);
        var github = new GitHub();
        Assert.Null(await Watch(github).TryResumeMergeableBlockedAsync(task));
        Assert.Equal(IssueStatus.Blocked, (await _issues.GetAsync(task.Id))!.Status);
        Assert.Null((await _issues.GetAsync(task.Id))!.GetMetadata("autoResumeAttempts"));
    }

    [Fact]
    public async Task ReleasingLongHumanWaitDoesNotImmediatelyExpireTheWatch()
    {
        var task = await Seed();
        var start = DateTimeOffset.UtcNow.AddDays(-2);
        task = await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, new Dictionary<string, object>
        { ["prOpenedAt"] = start.ToString("O"), ["humanReviewWaitStartedAt"] = start.AddMinutes(1).ToString("O") });
        var github = new GitHub();
        Assert.Equal(PRWatcher.WatchPollOutcome.Pending, await Watch(github).PollWatchedTaskAsync(task));
        Assert.Equal(1, github.Merges);
        Assert.True(ReviewWaitTiming.CompletedSeconds((await _issues.GetAsync(task.Id))!) > 86400);
    }

    [Fact]
    public void OldHumanObjectionSurvivesPushButDismissalClearsIt()
    {
        var objection = Decision("human", false, PullRequestReviewState.ChangesRequested, "old");
        Assert.True(EffectiveReview.Evaluate([objection], "head").OutstandingHumanObjection);
        Assert.Empty(EffectiveReview.Evaluate([objection], "head").CurrentRequests);
        Assert.False(EffectiveReview.Evaluate([objection, Decision("human", false, PullRequestReviewState.Dismissed, "old", 2)], "head").OutstandingHumanObjection);
    }

    [Theory]
    [InlineData("write", true)] [InlineData("maintain", true)] [InlineData("admin", true)]
    [InlineData("read", false)] [InlineData("triage", false)] [InlineData(null, false)]
    public void HumanMustHaveWritePermission(string? permission, bool expected) => Assert.Equal(expected, GitHubService.CanDecideReview(permission));

    [Theory]
    [InlineData("publisher[bot]", "publisher[bot]", true, false)]
    [InlineData("other[bot]", "author", true, false)]
    [InlineData("reviewer[bot]", "author", true, true)]
    [InlineData("human", "human", false, false)]
    [InlineData("human", "author", false, true)]
    public void OnlyIndependentConfiguredBotOrNonAuthorHumanIsEligible(string login, string author, bool bot, bool expected)
        => Assert.Equal(expected, GitHubService.IsReviewIdentityEligible(login, author, "reviewer[bot]", bot));
}
