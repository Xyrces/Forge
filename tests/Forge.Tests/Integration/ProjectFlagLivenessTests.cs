using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Consumers;
using Forge.Projects;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Project-flag liveness (D-a/D-b, incident 2026-08-23): $triage/$qa
/// enabled in the registry must reach the watch lane and every cached
/// reader without a process restart.
/// </summary>
public sealed class ProjectFlagLivenessTests : IDisposable
{
    private readonly string _dir;
    private readonly List<ProjectContextFactory> _factories = new();

    public ProjectFlagLivenessTests()
    {
        _dir = TempRoot.Instance.NewDirectory("flag-liveness");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var f in _factories)
        {
            try { f.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ProjectRecord Record(string id, bool triage = false, bool qa = false,
        IReadOnlyDictionary<string, RoleTerritory>? territories = null,
        IReadOnlyList<string>? verify = null) => new(
        Id: id, Name: id, RepoUrl: "https://example.invalid/repo.git", DefaultBranch: "main",
        LocalPath: null, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
        LastSyncedAt: null, LastSyncError: null,
        Roles: new Dictionary<string, int> { ["coredev"] = 3 },
        Territories: territories, VerifyCommands: verify,
        TriageEnabled: triage, QaEnabled: qa);

    private sealed class FakeProjectStore : IProjectStore
    {
        public readonly List<ProjectRecord> Records = new();
        public bool ThrowOnGet;
        public Task<Core.ProjectRecord> UpsertAsync(Core.NewProject project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Core.ProjectRecord?> GetAsync(string id, CancellationToken ct = default)
            => ThrowOnGet
                ? Task.FromException<Core.ProjectRecord?>(new InvalidOperationException("registry down"))
                : Task.FromResult(Records.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<ProjectRecord>> ListAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<ProjectRecord>)Records.ToList());
        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateLocalPathAsync(string id, string localPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSyncStatusAsync(string id, DateTime syncedAt, string? error, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateRolesAsync(string id, IReadOnlyDictionary<string, int> roles, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateTerritoriesAsync(string id, IReadOnlyDictionary<string, RoleTerritory> territories, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateVerifyCommandsAsync(string id, IReadOnlyList<string> commands, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateTriageAsync(string id, bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> UpdateQaAsync(string id, bool enabled, IReadOnlyList<string>? visualPaths = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public void LiveMode_FlagFlip_VisibleOnNextFind_BothDirections()
    {
        var store = new FakeProjectStore { Records = { Record("proj") } };
        var factory = new ProjectContextFactory(store, _dir);
        _factories.Add(factory);

        var ctx = factory.Find("proj");
        Assert.NotNull(ctx);
        Assert.False(ctx!.Options.TriageEnabled);
        Assert.False(ctx.Options.QaEnabled);

        // Flag PUT lands in the registry; the NEXT Find sees it without
        // a restart and without recreating the cached context (it owns
        // the shared IssueStore).
        store.Records[0] = store.Records[0] with { TriageEnabled = true, QaEnabled = true };
        var ctx2 = factory.Find("proj");
        Assert.Same(ctx, ctx2);
        Assert.True(ctx2!.Options.TriageEnabled);
        Assert.True(ctx2.Options.QaEnabled);

        store.Records[0] = store.Records[0] with { TriageEnabled = false, QaEnabled = false };
        var ctx3 = factory.Find("proj");
        Assert.False(ctx3!.Options.TriageEnabled);
        Assert.False(ctx3.Options.QaEnabled);
    }

    [Fact]
    public void LiveMode_RefreshFailure_ServesStaleSnapshot()
    {
        var store = new FakeProjectStore { Records = { Record("proj") } };
        var factory = new ProjectContextFactory(store, _dir);
        _factories.Add(factory);

        var ctx = factory.Find("proj");
        Assert.NotNull(ctx);
        Assert.False(ctx!.Options.QaEnabled);

        // Flip the flag, then take the registry down: the refresh must
        // degrade to the stale snapshot, not hard-fail the reader.
        store.Records[0] = store.Records[0] with { QaEnabled = true };
        store.ThrowOnGet = true;
        var ctx2 = factory.Find("proj");
        Assert.Same(ctx, ctx2);
        Assert.False(ctx2!.Options.QaEnabled);

        store.ThrowOnGet = false;
        Assert.True(factory.Find("proj")!.Options.QaEnabled);
    }

    [Fact]
    public void StaticMode_FlagFlip_NotRefreshed()
    {
        var projects = new List<ProjectOptions>
        {
            new() { Id = "proj", Name = "proj", RepoUrl = "", Root = _dir },
        };
        var factory = new ProjectContextFactory(projects);
        _factories.Add(factory);

        var ctx = factory.Find("proj");
        Assert.NotNull(ctx);
        Assert.False(ctx!.Options.QaEnabled);

        // Static mode's list is fixed for the process lifetime — even a
        // mutated list entry does not flow into the cached context.
        projects[0] = new ProjectOptions { Id = "proj", Name = "proj", RepoUrl = "", Root = _dir, QaEnabled = true };
        var ctx2 = factory.Find("proj");
        Assert.Same(ctx, ctx2);
        Assert.False(ctx2!.Options.QaEnabled);
    }

    private sealed class CapturingBundleFactory : IProjectDispatchBundleFactory
    {
        public ProjectOptions? Captured;
        public ProjectDispatchBundle Build(ProjectOptions project)
        {
            Captured = project;
            return null!;
        }
    }

    private sealed class ProbeConsumer : WatchConsumerBase<PrOpened>
    {
        public ProbeConsumer(Talaria.Core.Abstractions.ITransport transport, IProjectDispatchBundleFactory bundles, IProjectStore store)
            : base(transport, bundles, store, NullLogger<ProbeConsumer>.Instance) { }
        public Task<ProjectDispatchBundle?> ProbeAsync(string projectId)
            => BundleForAsync(projectId, NullLogger.Instance, CancellationToken.None);
        protected override Task HandleAsync(PrOpened evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task BundleForAsync_CarriesFlagsTerritoryVerify()
    {
        var store = new FakeProjectStore
        {
            Records =
            {
                Record("proj", triage: true, qa: true,
                    territories: new Dictionary<string, RoleTerritory>
                    {
                        ["coredev"] = new RoleTerritory(new List<string> { "src/" }, false),
                    },
                    verify: new List<string> { "dotnet test" }),
            },
        };
        var bundles = new CapturingBundleFactory();
        var consumer = new ProbeConsumer(new InMemoryTransport(), bundles, store);

        await consumer.ProbeAsync("proj");

        var captured = bundles.Captured;
        Assert.NotNull(captured);
        Assert.True(captured!.TriageEnabled);
        Assert.True(captured.QaEnabled);
        Assert.True(captured.Territories.ContainsKey("coredev"));
        Assert.Equal("src/", captured.Territories["coredev"].Prefixes.Single());
        Assert.Equal(new List<string> { "dotnet test" }, captured.VerifyCommands);
        Assert.Equal(3, captured.Roles["coredev"]);
    }
}

/// <summary>
/// Watch-sweep QA ordering (D-c): the QA-due evaluation runs BEFORE the
/// review-currency early-return, so a task whose review is already
/// current (or blocked by a fresh reviewStartedAt) still gets its QA
/// stage — the merge gate can't deadlock waiting on QA that never
/// launches.
/// </summary>
public sealed class WatchSweepQaOrderingTests : IDisposable
{
    private const string CodeHead = "c0dehead";
    private const string EvidenceHead = "ev1dence";

    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly InMemoryDashboardEventBus _events;
    private readonly FakeGitHub _gh;
    private readonly RecordingRunner _runner;
    private readonly ProjectDispatchBundle _bundle;
    private readonly WatchSweepService _sweep;

    public WatchSweepQaOrderingTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("sweep-qa");
        Directory.CreateDirectory(_workDir);
        var dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(dbPath, "test");
        _events = new InMemoryDashboardEventBus();
        _gh = new FakeGitHub();
        _runner = new RecordingRunner();
        var worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        var watcher = new PRWatcher(
            _gh, worktrees, _issues,
            TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), _events,
            NullLogger<PRWatcher>.Instance,
            lifecycle: new TaskStateMachine(writeAuthority: false, NullLogger.Instance),
            qaEnabled: true);
        _bundle = new ProjectDispatchBundle(
            project: new ProjectOptions
            {
                Id = "test", Name = "Test", RepoUrl = "", DefaultBranch = "main",
                Root = _workDir, QaEnabled = true,
            },
            issueStore: _issues,
            agents: new AgentStore(_issues),
            sprints: new SprintStore(_issues),
            designArtifacts: new DesignArtifactStore(dbPath),
            artOutputs: new ArtOutputStore(dbPath),
            worktrees: worktrees,
            gitHub: _gh,
            prWatcher: watcher,
            events: _events,
            logger: NullLogger<ProjectDispatchBundle>.Instance);
        _sweep = new WatchSweepService(
            _runner, llmConfig: null, modelOverrides: null,
            new ModelRateLimitTracker(), lifecycle: null, workflow: null,
            _events, NullLoggerFactory.Instance,
            NullLogger<WatchSweepService>.Instance);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private sealed class FakeGitHub : GitHubService
    {
        public string HeadSha = CodeHead;
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            // Octokit's Head is init-only; tests set it via reflection
            // (the sweep path has no headShaOverride seam).
            var pr = new PullRequest(number);
            var head = new GitReference(null!, null!, null!, "agent/task-x", HeadSha, null!, null!);
            typeof(PullRequest).GetProperty(nameof(PullRequest.Head))!.SetValue(pr, head);
            return Task.FromResult(pr);
        }
        public override Task<string> GetPullRequestDiffAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult("diff --git a/F.cs b/F.cs\n+added");
        public override Task<string> GetCompareDiffAsync(string baseSha, string headSha, CancellationToken cancellationToken = default)
            => Task.FromResult("diff --git a/F.cs b/F.cs\n+added");
        public override Task<IReadOnlyList<PrCommit>> GetCompareCommitsAsync(string baseSha, string headSha, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PrCommit>>(Array.Empty<PrCommit>());
        public override Task<IReadOnlyList<PrComment>> GetIssueCommentsAsync(int prNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PrComment>>(Array.Empty<PrComment>());
        public override Task<long> CreateIssueCommentAsync(long issueNumber, string body, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);
        public override Task<long> SubmitReviewAsync(int prNumber, string commitSha, string body, PullRequestReviewState state, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);
    }

    private sealed class RecordingRunner : IAgentRunner
    {
        public readonly List<AgentType> Calls = new();
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add(role);
            return Task.FromResult(new AgentRunResult("REVIEWER_VERDICT: APPROVE", null, 0, 0, TimeSpan.Zero));
        }
    }

    private async Task<IssueRecord> SeedAsync(Dictionary<string, object> meta)
    {
        meta["prNumber"] = "7";
        meta["worktreePath"] = _workDir;
        var task = await _issues.CreateAsync(new Core.NewIssue(Type: "dev", Title: "t", Metadata: meta));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null);
        return (await _issues.GetAsync(task.Id))!;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.True(await condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task QaDue_ReviewCurrent_LaunchesQa_ReviewWaits()
    {
        // The D-c deadlock shape: a review verdict already current at the
        // head AND a fresh reviewStartedAt (the review lane considers
        // itself busy). QA owes the head a verdict; the old ordering
        // early-returned on the review check and QA never launched.
        var reviewStartedSeed = DateTime.UtcNow.ToString("O");
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["branchSha"] = CodeHead,
            ["reviewSha"] = CodeHead,
            ["reviewVerdict"] = nameof(ReviewerVerdict.Approve),
            ["reviewStartedAt"] = reviewStartedSeed,
        });

        await _sweep.TryLaunchBackgroundReviewAsync(task, _bundle, CancellationToken.None);

        await WaitForAsync(async () =>
            (await _issues.GetAsync(task.Id))!.GetMetadata("qaStartedAt") is not null,
            TimeSpan.FromSeconds(10));
        var after = (await _issues.GetAsync(task.Id))!;
        // The review lane was NOT re-entered: its marker is untouched.
        Assert.Equal(reviewStartedSeed, after.GetMetadata("reviewStartedAt"));
        lock (_runner.Calls) Assert.DoesNotContain(AgentType.Reviewer, _runner.Calls);
    }

    [Fact]
    public async Task ExternalPush_WatchHeadShaMoved_RelaunchesQa()
    {
        // An external push moved the live head (recorded by the watcher
        // poll as watchHeadSha) past the QA-verified code head. Anchoring
        // to the observed head — not the frozen branchSha — must re-run
        // QA instead of deadlocking the gate.
        var reviewStartedSeed = DateTime.UtcNow.ToString("O");
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["branchSha"] = CodeHead,
            ["watchHeadSha"] = "externa1push",
            ["qaSha"] = EvidenceHead,
            ["qaForSha"] = CodeHead,
            ["qaVerdict"] = QaDispatcher.VerdictPass,
            ["reviewStartedAt"] = reviewStartedSeed,
        });
        _gh.HeadSha = "externa1push";

        await _sweep.TryLaunchBackgroundReviewAsync(task, _bundle, CancellationToken.None);

        await WaitForAsync(async () =>
            (await _issues.GetAsync(task.Id))!.GetMetadata("qaStartedAt") is not null,
            TimeSpan.FromSeconds(10));
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(reviewStartedSeed, after.GetMetadata("reviewStartedAt"));
        lock (_runner.Calls) Assert.DoesNotContain(AgentType.Reviewer, _runner.Calls);
    }

    [Fact]
    public async Task QaCurrentPass_LaunchesReview_QaNotRelaunched()
    {
        // QA passed at the code head and its evidence push moved the PR
        // head: qaSha = evidence head, qaForSha = code head. The sweep
        // must recognize QA as current (either sha matching branchSha)
        // and go straight to the review.
        _gh.HeadSha = EvidenceHead;
        var task = await SeedAsync(new Dictionary<string, object>
        {
            ["branchSha"] = CodeHead,
            ["qaForSha"] = CodeHead,
            ["qaSha"] = EvidenceHead,
            ["qaVerdict"] = QaDispatcher.VerdictPass,
        });

        await _sweep.TryLaunchBackgroundReviewAsync(task, _bundle, CancellationToken.None);

        // Wait for the review to run to completion (verdict recorded),
        // not just the start stamp — the verdict landing clears
        // reviewStartedAt, and asserting on a mid-flight marker races.
        await WaitForAsync(async () =>
            (await _issues.GetAsync(task.Id))!.GetMetadata("reviewVerdict") is not null,
            TimeSpan.FromSeconds(15));
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(nameof(ReviewerVerdict.Approve), after.GetMetadata("reviewVerdict"));
        // QA was current — no QA run launched.
        Assert.Null(after.GetMetadata("qaStartedAt"));

        // A second pass must not double-launch anything: the reviewer
        // dedupes on reviewSha == head and QA stays untouched.
        var verdict = after.GetMetadata("reviewVerdict");
        await _sweep.TryLaunchBackgroundReviewAsync(after, _bundle, CancellationToken.None);
        await Task.Delay(500);
        var final = (await _issues.GetAsync(task.Id))!;
        Assert.Null(final.GetMetadata("qaStartedAt"));
        Assert.Equal(verdict, final.GetMetadata("reviewVerdict"));
        lock (_runner.Calls) Assert.Equal(1, _runner.Calls.Count(r => r == AgentType.Reviewer));
    }
}

/// <summary>
/// The watcher poll records the observed live head (watchHeadSha) —
/// the sweep's QA-due check anchors to it, so external pushes and QA
/// evidence pushes must move it.
/// </summary>
public sealed class WatchHeadShaRecordingTests : IDisposable
{
    private const string Head = "1ivehead42";

    private readonly string _workDir;
    private readonly IssueStore _issues;

    public WatchHeadShaRecordingTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("watch-head");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private sealed class FakeGitHub : GitHubService
    {
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
        {
            var pr = new PullRequest(number);
            typeof(PullRequest).GetProperty(nameof(PullRequest.ChangedFiles))!
                .SetValue(pr, 1);
            return Task.FromResult(pr);
        }
        public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(CommitState.Pending);
        public override Task<int> GetCiSignalCountAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(1);
        public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<PullRequestReview>)new List<PullRequestReview>());
    }

    [Fact]
    public async Task Poll_RecordsObservedLiveHead()
    {
        var task = await _issues.CreateAsync(new Core.NewIssue(
            "task", "implement X",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = "42",
                ["branch"] = "agent/task-x",
            }));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null);
        var watcher = new PRWatcher(
            new FakeGitHub(),
            worktrees: new GitWorktreeService(
                new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
                NullLogger<GitWorktreeService>.Instance),
            issues: _issues,
            pollInterval: TimeSpan.FromSeconds(1),
            staleAfter: TimeSpan.FromHours(1),
            events: new InMemoryDashboardEventBus(),
            logger: NullLogger<PRWatcher>.Instance,
            lifecycle: new TaskStateMachine(writeAuthority: false, NullLogger.Instance),
            qaEnabled: false);

        await watcher.PollWatchedTaskAsync(
            (await _issues.GetAsync(task.Id))!, CancellationToken.None, headShaOverride: _ => Head);

        Assert.Equal(Head, (await _issues.GetAsync(task.Id))!.GetMetadata("watchHeadSha"));
    }
}
