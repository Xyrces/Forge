using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Forge;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Reviewer;
using Forge.Projects;
using Forge.Orchestrator.Workflow;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="OrchestratorAgent"/> that exercise the
/// claim-runner-commit metadata pipeline against real git + sqlite, but
/// with a scripted <see cref="IAgentRunner"/> so no LLM is required.
///
/// <para>
/// In the v1 → multi-project migration the orchestrator went from a
/// single primary-project bundle to a <see cref="ProjectDispatchBundle"/>
/// per registered project. These tests construct a single bundle
/// directly (the same path the dispatch loop uses) and call
/// <see cref="OrchestratorAgent.DispatchSingleTaskAsync"/> with it.
/// </para>
/// </summary>
public sealed class OrchestratorAgentTests : IDisposable
{
    private const string DevTaskType = "dev";
    private readonly string _workDir;
    private readonly string _dataRoot;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly ProjectStore _projectStore;
    private readonly AgentMessageBus _messageBus;
    private readonly InMemoryDashboardEventBus _events;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly GitWorktreeService _worktrees;
    private readonly GitHubService _github;
    private readonly PRWatcher _prWatcher;
    private readonly ProjectDispatchBundle _bundle;
    private readonly string _originalCwd;

    public OrchestratorAgentTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("orch");
        _dataRoot = TempRoot.Instance.NewDirectory("orch-data");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_dataRoot);

        InitRepo(_workDir);

        _dbPath = Path.Combine(_dataRoot, "issues.db");
        _issues = new IssueStore(_dbPath);
        _projectStore = new ProjectStore(_issues);

        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        _messageBus = new AgentMessageBus();
        _events = new InMemoryDashboardEventBus();
        _roleRegistry = new RoleAgentRegistry();
        _github = new GitHubService("", "", "");
        _prWatcher = new PRWatcher(
            _github, _worktrees, _issues,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), _events,
            NullLogger<PRWatcher>.Instance);

        _bundle = new ProjectDispatchBundle(
            project: new ProjectOptions
            {
                Id = "test",
                Name = "Test",
                RepoUrl = "",
                DefaultBranch = "main",
                Root = _workDir,
            },
            issueStore: _issues,
            agents: new Core.AgentStore(_issues),
            sprints: new Core.SprintStore(_issues),
            designArtifacts: new Core.DesignArtifactStore(_dbPath),
            artOutputs: new Core.ArtOutputStore(_dbPath),
            worktrees: _worktrees,
            gitHub: _github,
            prWatcher: _prWatcher,
            events: _events,
            logger: NullLogger<ProjectDispatchBundle>.Instance);

        _originalCwd = Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Sprint flow: engineering dispatch only claims tasks linked to
    /// the ACTIVE sprint. Tests that expect a dispatch must activate
    /// a sprint containing the task first.
    /// </summary>
    private async Task<string> ActivateSprintWithAsync(params string[] issueIds)
    {
        var sprint = await _bundle.Sprints.CreateAsync(new Core.NewSprint(
            Name: "test sprint", Goal: "g",
            StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(1),
            Status: Core.SprintStatus.Active));
        foreach (var id in issueIds)
        {
            await _bundle.Sprints.AddIssueAsync(sprint.Id, id);
            // The real assembler only adds groomed work; the dispatch
            // loop refuses ungroomed parentless members (operator rule
            // 2026-07-23/31). Mirror the invariant here.
            var issue = await _issues.GetAsync(id);
            if (issue is not null && issue.ParentIssueId is null
                && issue.GetMetadata("groomed") is null)
            {
                var meta = new Dictionary<string, object>();
                if (!string.IsNullOrWhiteSpace(issue.MetadataJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(issue.MetadataJson);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        meta[p.Name] = p.Value.GetRawText();
                    }
                }
                meta["groomed"] = "true";
                await _issues.TransitionAsync(id, issue.Status, error: null, metadata: meta);
            }
        }
        return sprint.Id;
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        try { Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

    private Forge.Orchestrator.WatchSweepService BuildSweepService(IAgentRunner runner)
        => new Forge.Orchestrator.WatchSweepService(
            runner,
            llmConfig: null,
            modelOverrides: null,
            new Core.ModelRateLimitTracker(),
            lifecycle: null,
            workflow: null,
            _events,
            NullLoggerFactory.Instance,
            NullLogger<Forge.Orchestrator.WatchSweepService>.Instance);

    private OrchestratorAgent BuildOrchestrator(IAgentRunner runner)
        => new OrchestratorAgent(
            _projectStore,
            new StubBundleFactory(_bundle),
            runner,
            _roleRegistry,
            _messageBus,
            new InProcessDispatcher(
                (issue, bundle, ct) => RunWorkflowInProcess(runner, issue, ct),
                NullLogger<InProcessDispatcher>.Instance),
            _events,
            NullLogger<OrchestratorAgent>.Instance);

    private async Task RunWorkflowInProcess(IAgentRunner runner, IssueRecord issue, CancellationToken ct)
    {
        var workflow = new EngineeringDispatchWorkflow(
            issues: _issues,
            agentRunner: runner,
            worktrees: _worktrees,
            gitHub: _github,
            roleRegistry: _roleRegistry,
            workspaceOptions: new Configuration.WorkspaceOptions
            {
                Root = _workDir, WorktreeRoot = ".portHorizon/worktrees",
                DefaultBranch = "main",
            },
            events: _events,
            drainMessageBus: agent => _messageBus.Drain(agent),
            designArtifacts: new Core.DesignArtifactStore(_dbPath),
            artOutputs: new Core.ArtOutputStore(_dbPath),
            memoryExtractor: new Forge.Orchestrator.NoOpMemoryExtractor(),
            extractionStore: new Forge.Orchestrator.MemoryExtractionStore(_dbPath),
            logger: NullLogger<EngineeringDispatchWorkflow>.Instance);
        await workflow.RunAsync(issue, ct);
    }

    private void BindMaf(OrchestratorAgent orch)
    {
        orch.BindOptions(new AgentOptions
        {
            Workspace = new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            Spawner = new SpawnerOptions { MaxConcurrentSessions = 1, PollIntervalSeconds = 1 },
            // Per-model cooldown keys: CoreDev resolves to the default
            // model, ClientDev is pinned to a second model on the same
            // provider (the minimax vs kimi-k3 quota split).
            Llm = new LlmOptions
            {
                Providers =
                {
                    new LlmProviderOptions { Name = "kilo-gateway", BaseUrl = "http://stub", DefaultModel = "minimax/minimax-m3" },
                },
                DefaultProvider = "kilo-gateway",
                Roles = { ["ClientDev"] = new LlmRoleModelOptions { ProviderName = "kilo-gateway", Model = "kimi-k3" } },
            },
        });
    }

    [Fact]
    public async Task DispatchCycle_ModelCooldown_OtherModelStillClaims()
    {
        // Per-model 429 cooldowns (operator ask 2026-07-24): quotas
        // live at the (provider, model) boundary — a 429 from minimax
        // must NOT freeze tasks that would run on kimi-k3.
        var runner = new CoreDevRateLimitedRunner();
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));
        var orch = BuildOrchestrator(runner);
        BindMaf(orch);

        // Sprint starts with only the dev task: it claims, 429s, and
        // arms the minimax cooldown.
        var dev = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "dev", Description: "x"));
        var sprintId = await ActivateSprintWithAsync(dev.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = orch.ExecuteAsync(cts.Token);
        await WaitForAsync(() => Task.FromResult(runner.CoreDev429d), TimeSpan.FromSeconds(15));
        await WaitForAsync(async () =>
            (await _issues.GetAsync(dev.Id))!.Status == IssueStatus.Pending, TimeSpan.FromSeconds(10));

        // NOW a kimi-k3 task enters the sprint — mid-cooldown for
        // minimax. A global cooldown would hold it for 3 minutes;
        // per-model lets it claim immediately.
        var ui = await _issues.CreateAsync(new NewIssue(Type: "ui", Title: "ui task", Description: "x",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await _bundle.Sprints.AddIssueAsync(sprintId, ui.Id);

        await WaitForAsync(async () =>
        {
            if (loop.IsFaulted) Assert.Fail($"dispatch loop faulted: {loop.Exception?.GetBaseException()}");
            return (await _issues.GetAsync(ui.Id))!.Status is IssueStatus.InProgress or IssueStatus.Completed;
        }, TimeSpan.FromSeconds(15));
        // The minimax task must NOT have been reclaimed in the meantime.
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(dev.Id))!.Status);

        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    private sealed class CoreDevRateLimitedRunner : IAgentRunner
    {
        public volatile bool CoreDev429d;
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => role == AgentType.CoreDev
                ? Throw429()
                : Task.FromResult(new AgentRunResult("done. NO_CHANGES_NEEDED", null, 1, 1, TimeSpan.FromMilliseconds(1)));
        private Task<AgentRunResult> Throw429()
        {
            CoreDev429d = true;
            return Task.FromException<AgentRunResult>(
                new HttpRequestException("Error 429 Too Many Requests: rate limit reached"));
        }
    }

    [Fact]
    public async Task DispatchSingleTask_MafPath_NoOpBranch_CapturesModelResponse()
    {
        const string scripted = "I'll add a feature but make no edits. NO_CHANGES_NEEDED";
        var orch = BuildOrchestrator(new ScriptedRunner(scripted));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "Add a feature", Description: "Please do the thing"));

        var result = await orch.DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);

        Assert.True(result.Success, $"expected success, got: {result.Message}");
        var after = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Completed, after.Status);
        Assert.Equal(scripted, after.GetMetadata("modelResponse"));
    }

    [Fact]
    public async Task DispatchSingleTask_StaleLastError_DoesNotFailSuccessfulRun()
    {
        // Regression (observed live 2026-07-24): a failed run records
        // lastError in metadata; requeues never clear it. The next
        // SUCCESSFUL dispatch read the stale error post-workflow and
        // flipped the task to Failed. The freshness guard + clear-on-
        // success fix this.
        var orch = BuildOrchestrator(new ScriptedRunner("verified, nothing to do. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "x", Description: "y",
            Metadata: new Dictionary<string, object>
            {
                ["lastError"] = "ArgumentOutOfRangeException: from yesterday's run",
                ["lastErrorAt"] = DateTimeOffset.UtcNow.AddHours(-6).ToString("O"),
            }));

        var result = await orch.DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);

        Assert.True(result.Success, $"expected success, got: {result.Message}");
        var after = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Completed, after.Status);
        Assert.True(string.IsNullOrEmpty(after.GetMetadata("lastError")));
    }

    [Fact]
    public async Task DispatchSingleTask_AlreadyClaimed_ReturnsAlreadyClaimed()
    {
        var orch = BuildOrchestrator(new ScriptedRunner("ok. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "x", Description: "y"));

        var first = await orch.DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);
        Assert.True(first.Success);

        var issueRefresh = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        var second = await orch.DispatchSingleTaskAsync(issueRefresh, _bundle, CancellationToken.None);
        Assert.False(second.Success);
        Assert.Equal("already-claimed", second.Message);
    }

    [Fact]
    public async Task DispatchSingleTask_OperatorMessageBusIncludedInPrompt()
    {
        var capture = new CapturingRunner("ok");
        var orch = BuildOrchestrator(capture);
        BindMaf(orch);

        _messageBus.Enqueue("coredev", "Don't forget the README.");

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "x", Description: "y"));

        await orch.DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);

        Assert.NotNull(capture.LastPrompt);
        Assert.Contains("Don't forget the README.", capture.LastPrompt!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchCycle_ProjectStoreList_PicksUpRegisteredProjects()
    {
        // Seed the project store directly (the dispatch loop's
        // ProjectStore is the source of truth now — appsettings.json
        // seeding is gone). Use the test's own local git repo as
        // the URL — it satisfies the non-empty RepoUrl contract and
        // is cloneable by the production ProjectCloner if it ever
        // runs for this id (it won't, because the test bundle is
        // already constructed and the dispatch loop's factory
        // would short-circuit on the existing bundle cache key).
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));

        var orch = BuildOrchestrator(new ScriptedRunner("ok. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "Run via store", Description: "Cycle"));
        await ActivateSprintWithAsync(issue.Id);

        // Call the dispatch loop directly. CancellationToken with
        // immediate cancellation after the first cycle; the loop is
        // an infinite while-loop.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await orch.ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }

        // The cycle should have claimed and dispatched the issue via
        // the bundle the factory returned. The exact outcome
        // (success/no-diff) depends on the workflow; we assert
        // status != Pending to prove dispatch ran.
        var after = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        Assert.NotEqual(IssueStatus.Pending, after.Status);
    }

    [Fact]
    public async Task DispatchCycle_SkipsPipelineContainers()
    {
        // Epics and stories feed the spec -> groom chain; the
        // engineering loop must never claim them (UI e2e finding:
        // an intake-accepted epic was implemented directly).
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));

        var orch = BuildOrchestrator(new ScriptedRunner("ok. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var epic = await _issues.CreateAsync(new NewIssue(
            Type: "epic", Title: "container epic", Description: "should not dispatch"));
        var story = await _issues.CreateAsync(new NewIssue(
            Type: "story", Title: "container story", Description: "should not dispatch"));
        // A real task queued BEHIND containers must still dispatch
        // (queue-head starvation regression: ReadyAsync's LIMIT
        // applied before the container filter).
        var task = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "real work", Description: "must dispatch despite containers ahead"));
        var watch = await _issues.CreateAsync(new NewIssue(
            Type: "pr-watch", Title: "watch issue", Description: "routes to the watcher, not engineering",
            Metadata: new Dictionary<string, object> { ["prNumber"] = 999, ["taskId"] = "task-x" }));
        // Sprint flow: only sprint members dispatch. The containers
        // and the watch stay unlinked (they are never sprint work).
        await ActivateSprintWithAsync(task.Id);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await orch.ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }

        var epicAfter = (await _issues.GetAsync(epic.Id, CancellationToken.None))!;
        var storyAfter = (await _issues.GetAsync(story.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Pending, epicAfter.Status);
        Assert.Equal(IssueStatus.Pending, storyAfter.Status);
        Assert.Null(epicAfter.Assignee);
        Assert.Null(storyAfter.Assignee);

        // The real task was claimed despite the containers ahead of it.
        var taskAfter = (await _issues.GetAsync(task.Id, CancellationToken.None))!;
        Assert.NotEqual(IssueStatus.Pending, taskAfter.Status);

        // The pr-watch issue must never be claimed by ENGINEERING
        // dispatch (regression: the container filter briefly let it
        // through and the watch was closed as a no-op commit). The
        // watch path (PrWatcher stub in the bundle) may or may not
        // have claimed it — what matters is it was not marked
        // Completed by the no-diff engineering path.
        var watchAfter = (await _issues.GetAsync(watch.Id, CancellationToken.None))!;
        Assert.NotEqual(IssueStatus.Completed, watchAfter.Status);
    }

    [Fact]
    public async Task DispatchCycle_NoActiveSprint_DevTaskStaysPending()
    {
        // Sprint flow: ALL engineering work happens inside a sprint.
        // With no active sprint the gate holds every dev task, even
        // an otherwise-ready one; the SprintAssembler owns ingest.
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));

        var orch = BuildOrchestrator(new ScriptedRunner("ok. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var gated = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "gated work", Description: "no sprint -> no dispatch"));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try { await orch.ExecuteAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        var after = (await _issues.GetAsync(gated.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Null(after.Assignee);
    }

    [Fact]
    public async Task DispatchCycle_TaskOutsideActiveSprint_StaysPending()
    {
        // Sprint flow: with a sprint active, only its members
        // dispatch — Pending work outside the sprint waits for a
        // later sprint's ingest.
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));

        var orch = BuildOrchestrator(new ScriptedRunner("ok. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var inSprint = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "sprint work", Description: "member"));
        var outside = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "future work", Description: "not a member"));
        await ActivateSprintWithAsync(inSprint.Id);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try { await orch.ExecuteAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        var inAfter = (await _issues.GetAsync(inSprint.Id, CancellationToken.None))!;
        Assert.NotEqual(IssueStatus.Pending, inAfter.Status);
        var outAfter = (await _issues.GetAsync(outside.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Pending, outAfter.Status);
        Assert.Null(outAfter.Assignee);
    }

    private static void InitRepo(string path)
    {
        RunGit(path, "init", "-q -b main");
        RunGit(path, "config", "user.email a@b");
        RunGit(path, "config", "user.name a");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test\n");
        RunGit(path, "add", "README.md");
        RunGit(path, "commit", "-q -m initial");
    }

    private static void RunGit(string cwd, string verb, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.ArgumentList.Add(verb);
        foreach (var part in args.Split(' ')) psi.ArgumentList.Add(part);
        using var p = Process.Start(psi)!;
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {verb} {args} (cwd={cwd}) failed: {err}");
        }
    }

    [Fact]
    public async Task DispatchCycle_RoleCaps_SameRoleRunsInParallelUpToCap()
    {
        // Per-role parallelism: the flat MaxConcurrentSessions cap was
        // replaced by per-(project, role) SlotTable pools (default
        // coredev=2). Two unblocked dev tasks must run CONCURRENTLY;
        // the third waits for a same-role slot to free.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new BlockingRunner(gate);
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));
        var orch = BuildOrchestrator(runner);
        BindMaf(orch);

        var first = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "first", Description: "x"));
        var second = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "second", Description: "x"));
        var third = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "third", Description: "x"));
        await ActivateSprintWithAsync(first.Id, second.Id, third.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = orch.ExecuteAsync(cts.Token);

        // Both free coredev slots fill — the two runs block on the gate.
        await WaitForAsync(async () =>
        {
            if (loop.IsFaulted) Assert.Fail($"dispatch loop faulted: {loop.Exception?.GetBaseException()}");
            return (await _issues.GetAsync(first.Id))!.Status == IssueStatus.InProgress
                && (await _issues.GetAsync(second.Id))!.Status == IssueStatus.InProgress;
        }, TimeSpan.FromSeconds(15));
        // Same-role cap reached: the third task must NOT be claimed.
        await Task.Delay(2500);
        Assert.Equal(IssueStatus.Pending, (await _issues.GetAsync(third.Id))!.Status);

        gate.SetResult();
        await WaitForAsync(async () =>
            (await _issues.GetAsync(third.Id))!.Status != IssueStatus.Pending, TimeSpan.FromSeconds(15));
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DispatchCycle_RoleCaps_FullPoolDoesNotBlockOtherRoles()
    {
        // Operator model (2026-07-24): "if any task is unblocked, it
        // should be picked up by a dev agent as soon as one is free."
        // A full coredev pool must not starve a clientdev task.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new BlockingRunner(gate);
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));
        var orch = BuildOrchestrator(runner);
        BindMaf(orch);

        var dev1 = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "dev1", Description: "x"));
        var dev2 = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "dev2", Description: "x"));
        var ui = await _issues.CreateAsync(new NewIssue(Type: "ui", Title: "ui task", Description: "x"));
        await ActivateSprintWithAsync(dev1.Id, dev2.Id, ui.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = orch.ExecuteAsync(cts.Token);

        // Both coredev slots are held — the clientdev task still claims.
        await WaitForAsync(async () =>
        {
            if (loop.IsFaulted) Assert.Fail($"dispatch loop faulted: {loop.Exception?.GetBaseException()}");
            return (await _issues.GetAsync(dev1.Id))!.Status == IssueStatus.InProgress
                && (await _issues.GetAsync(dev2.Id))!.Status == IssueStatus.InProgress;
        }, TimeSpan.FromSeconds(15));
        await WaitForAsync(async () =>
            (await _issues.GetAsync(ui.Id))!.Status == IssueStatus.InProgress, TimeSpan.FromSeconds(10));

        cts.Cancel();
        gate.SetResult();
        try { await loop; } catch (OperationCanceledException) { }
    }

    private static async Task WaitForAsync(Func<Task<bool>> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await cond()) return;
            await Task.Delay(100);
        }
        Assert.Fail("condition not met within timeout");
    }

    [Fact]
    public async Task ShouldLaunchReview_SkipsFreshMarkerAndInFlight()
    {
        // Off-loop reviews (2026-07-31): the sweep must not stack
        // launches — a fresh reviewStartedAt marker (review running,
        // possibly started pre-restart) or an in-memory in-flight
        // entry both suppress relaunch.
        var sweeps = BuildSweepService(new ScriptedRunner("APPROVE"));
        var task = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "t", Description: "x"));

        // No marker, nothing in flight → launch.
        Assert.True(sweeps.ShouldLaunchReview(task, "test"));

        // Fresh marker → skip.
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object> { ["reviewStartedAt"] = DateTime.UtcNow.ToString("O") });
        task = (await _issues.GetAsync(task.Id))!;
        Assert.False(sweeps.ShouldLaunchReview(task, "test"));

        // Stale marker (crashed/restarted mid-review) → relaunch.
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object> { ["reviewStartedAt"] = DateTime.UtcNow.AddMinutes(-30).ToString("O") });
        task = (await _issues.GetAsync(task.Id))!;
        Assert.True(sweeps.ShouldLaunchReview(task, "test"));
    }

    private async Task<IssueRecord> SeedBlockedWatchAsync(Dictionary<string, object> metadata)
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: DevTaskType, Title: "watched", Description: "x"));
        metadata["prNumber"] = "123";
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "blocked for test", metadata);
        return (await _issues.GetAsync(task.Id))!;
    }

    [Fact]
    public async Task WatchSweep_TransientReviewerBlock_AutoResumes()
    {
        // Unblock nudge: a task Blocked solely because the reviewer
        // model was unavailable (blockedKind=reviewer-unavailable) is
        // resumed by the sweep once the model is back — status back to
        // InProgress, stale review bookkeeping cleared, marker removed.
        var sweeps = BuildSweepService(new ScriptedRunner("APPROVE"));
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));
        var task = await SeedBlockedWatchAsync(new Dictionary<string, object>
        {
            ["blockedKind"] = Forge.Reviewer.PRWatcher.BlockedKindReviewerUnavailable,
            ["reviewVerdict"] = "Error",
            ["reviewSha"] = "abc123",
            ["reviewRound"] = "3",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sweeps.SweepProjectAsync(_bundle, cts.Token);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.InProgress, after.Status);
        Assert.Equal("1", after.GetMetadata("autoResumeAttempts"));
        Assert.Null(after.GetMetadata("blockedKind"));
    }

    [Fact]
    public async Task WatchSweep_GenuineBlock_StaysBlocked()
    {
        // Operator-decision blocks (no blockedKind marker) must NOT be
        // auto-resumed — the sweep leaves them alone.
        var sweeps = BuildSweepService(new ScriptedRunner("APPROVE"));
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));
        var task = await SeedBlockedWatchAsync(new Dictionary<string, object>
        {
            ["reworkAttempts"] = "3",
            ["reworkReason"] = "PR conflicts with the base branch",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sweeps.SweepProjectAsync(_bundle, cts.Token);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Blocked, after.Status);
        Assert.Null(after.GetMetadata("autoResumeAttempts"));
    }

    [Fact]
    public async Task WatchSweep_AutoResumeBudgetExhausted_ClearsMarkerAndStaysBlocked()
    {
        var sweeps = BuildSweepService(new ScriptedRunner("APPROVE"));
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));
        var task = await SeedBlockedWatchAsync(new Dictionary<string, object>
        {
            ["blockedKind"] = Forge.Reviewer.PRWatcher.BlockedKindReviewerUnavailable,
            ["autoResumeAttempts"] = "3",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sweeps.SweepProjectAsync(_bundle, cts.Token);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Blocked, after.Status);
        Assert.Null(after.GetMetadata("blockedKind"));
    }

    [Fact]
    public async Task DispatchSingleTask_ReworkRound_SyncsWorktreeToRemotePrHead()
    {
        // Rework-round dispatch: a task with prNumber + reworkAttempts > 0
        // must have its worktree branch synced to the remote PR head before
        // the agent runs, even when the local checkout has diverged.
        // Use the issue's actual id as the branch name so the remote ref
        // matches the workflow's convention: "agent/{issue.Id}".
        var orch = BuildOrchestrator(new ScriptedRunner("verified, nothing to do. NO_CHANGES_NEEDED"));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "rework round", Description: "sync test"));

        // Set up a remote with a PR branch that matches the dispatch convention.
        var taskId = issue.Id; // The workflow branch will be "agent/{issue.Id}"
        var bareDir = Path.Combine(Path.GetTempPath(), "ph-orch-remote-" + Guid.NewGuid().ToString("N"));
        RunGit(Path.GetTempPath(), "init", "-q --bare " + bareDir);
        RunGit(_workDir, "remote", "add origin " + bareDir);
        RunGit(_workDir, "push", "-u origin main");
        var branch = "agent/" + taskId;
        RunGit(_workDir, "checkout", "-b " + branch);
        File.WriteAllText(Path.Combine(_workDir, "rework-content.txt"), "from-remote-pr-head");
        RunGit(_workDir, "add", "rework-content.txt");
        RunGit(_workDir, "commit", "-q -m PRhead");
        RunGit(_workDir, "push", "-u origin " + branch);
        RunGit(_workDir, "checkout", "main");

        // Verify the remote PR head is different from main.
        var remoteSha = RunGitCaptureSha(_workDir, "rev-parse origin/" + branch);
        var mainSha = RunGitCaptureSha(_workDir, "rev-parse main");
        Assert.NotEqual(remoteSha, mainSha);

        // Now set the rework metadata on the issue.
        await _issues.TransitionAsync(issue.Id, IssueStatus.Pending, null, new Dictionary<string, object>
        {
            ["prNumber"] = "42",
            ["reworkAttempts"] = "2",
        });

        // Dispatch.
        var result = await orch.DispatchSingleTaskAsync(issue, _bundle, CancellationToken.None);
        Assert.True(result.Success, $"expected success, got: {result.Message}");

        // Verify: after the workflow completes, the worktree
        // should be at the remote PR head (not main).
        var worktreePath = _worktrees.WorktreePathFor(issue.Id);
        Assert.True(Directory.Exists(worktreePath),
            "Worktree directory should exist after dispatch");

        var wtSha = RunGitCaptureSha(worktreePath, "rev-parse HEAD");
        Assert.Equal(remoteSha, wtSha);

        Assert.True(File.Exists(Path.Combine(worktreePath, "rework-content.txt")),
            "Worktree should have rework-content.txt from the remote PR head after sync");
    }

    /// <summary>Run git and capture trimmed output. Throws on non-zero exit.</summary>
    private static string RunGitCaptureSha(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} (cwd={dir}) failed (exit={p.ExitCode}): {err}");
        return output;
    }

    private sealed class BlockingRunner : IAgentRunner
    {
        private readonly TaskCompletionSource _gate;
        public BlockingRunner(TaskCompletionSource gate) { _gate = gate; }
        public async Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context, CancellationToken ct)
        {
            await _gate.Task;
            return new AgentRunResult("done. NO_CHANGES_NEEDED", null, 1, 1, TimeSpan.FromMilliseconds(1));
        }
    }

    private sealed class ScriptedRunner : IAgentRunner
    {
        private readonly string _response;
        public ScriptedRunner(string response) { _response = response; }
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => Task.FromResult(new AgentRunResult(_response, null, 1, 1, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class CapturingRunner : IAgentRunner
    {
        private readonly string _response;
        public string? LastPrompt { get; private set; }
        public CapturingRunner(string response) { _response = response; }
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new AgentRunResult(_response, null, 1, 1, TimeSpan.FromMilliseconds(1)));
        }
    }

    /// <summary>
    /// Test double for <see cref="ProjectDispatchBundleFactory"/> —
    /// returns the bundle the test set up directly (skipping the
    /// lazy ProjectBootstrap + cloner wiring). Production code goes
    /// through <see cref="ProjectDispatchBundleFactory.Build"/> which
    /// uses <see cref="Projects.ProjectBootstrap.EnsureProject"/>.
    /// </summary>
    private sealed class StubBundleFactory : IProjectDispatchBundleFactory
    {
        private readonly ProjectDispatchBundle _bundle;
        public StubBundleFactory(ProjectDispatchBundle bundle) { _bundle = bundle; }
        public ProjectDispatchBundle Build(ProjectOptions project) => _bundle;
    }
}
