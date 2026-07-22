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
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-orch-{Guid.NewGuid():N}");
        _dataRoot = Path.Combine(Path.GetTempPath(), $"ph-orch-data-{Guid.NewGuid():N}");
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

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        try { Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

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
        });
    }

    [Fact]
    public async Task DispatchSingleTask_MafPath_NoOpBranch_CapturesModelResponse()
    {
        const string scripted = "I'll add a feature but make no edits.";
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
    public async Task DispatchSingleTask_AlreadyClaimed_ReturnsAlreadyClaimed()
    {
        var orch = BuildOrchestrator(new ScriptedRunner("ok"));
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

        var orch = BuildOrchestrator(new ScriptedRunner("ok"));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "Run via store", Description: "Cycle"));

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
