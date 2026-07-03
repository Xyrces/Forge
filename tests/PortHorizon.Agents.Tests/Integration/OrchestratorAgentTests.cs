using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Orchestrator;
using PortHorizon.Agents.Reviewer;
using PortHorizon.Agents.Orchestrator.Workflow;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="OrchestratorAgent"/> that exercise the
/// full claim-runner-commit metadata pipeline against real git + sqlite, but
/// with a scripted <see cref="IAgentRunner"/> so no LLM is required.
///
/// <para>
/// These tests run the "no-commit" branch of <c>DispatchSingleTaskAsync</c>
/// (agent runs, no files edited, mark Completed with lastResponse captured).
/// This is the safest branch to test without a real GitHub remote.
/// </para>
///
/// <para>
/// P0 deliverable: the Maf path is wired end-to-end. <c>Runtime=Acp</c>
/// must throw a <see cref="NotSupportedException"/> because the kilo path
/// is staged for removal.
/// </para>
/// </summary>
public sealed class OrchestratorAgentTests : IDisposable
{
    private const string DevTaskType = "dev";
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IIssueStore _issues;
    private readonly AgentMessageBus _messageBus;
    private readonly InMemoryDashboardEventBus _events;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly GitWorktreeService _worktrees;
    private readonly GitHubService _github;
    private readonly PRWatcher _prWatcher;
    private readonly string _originalCwd;

    public OrchestratorAgentTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-orch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");

        InitRepo(_workDir);

        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        _issues = new IssueStore(_dbPath);
        _messageBus = new AgentMessageBus();
        _events = new InMemoryDashboardEventBus();
        _roleRegistry = new RoleAgentRegistry();
        _github = new GitHubService("", "", "");
        _prWatcher = new PRWatcher(
            _github, _worktrees, _issues,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), _events,
            NullLogger<PRWatcher>.Instance);

        _originalCwd = Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private OrchestratorAgent BuildOrchestrator(IAgentRunner runner)
        => new OrchestratorAgent(
            runner,
            _roleRegistry,
            _worktrees,
            _github,
            _prWatcher,
            _issues,
            new Core.AgentStore((IssueStore)_issues),
            new Core.SprintStore((IssueStore)_issues),
            _messageBus,
            _events,
            new Core.DesignArtifactStore(_dbPath),
            new Core.ArtOutputStore(_dbPath),
            // InProcessDispatcher — reuses the existing
            // EngineeringDispatchWorkflow + InProcessExecution
            // path; Stage A's behavior. Stage B's DurableDispatcher
            // has its own integration test (B.6).
            new InProcessDispatcher(
                (issue, ct) => RunWorkflowInProcess(runner, issue, ct),
                NullLogger<InProcessDispatcher>.Instance),
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

        var result = await orch.DispatchSingleTaskAsync(issue, CancellationToken.None);

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

        var first = await orch.DispatchSingleTaskAsync(issue, CancellationToken.None);
        Assert.True(first.Success);

        var issueRefresh = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        var second = await orch.DispatchSingleTaskAsync(issueRefresh, CancellationToken.None);
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

        await orch.DispatchSingleTaskAsync(issue, CancellationToken.None);

        Assert.NotNull(capture.LastPrompt);
        Assert.Contains("Don't forget the README.", capture.LastPrompt!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchSingleTask_PublishesTransitionEvents()
    {
        var orch = BuildOrchestrator(new ScriptedRunner("ok"));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "x", Description: "y"));

        await orch.DispatchSingleTaskAsync(issue, CancellationToken.None);

        var snapshot = _events.GetHistorySnapshot();
        var kinds = snapshot.Select(e => e.Kind).ToList();
        Assert.Contains(DashboardEventKind.TaskTransition, kinds);
        Assert.Contains(DashboardEventKind.AgentSessionStarted, kinds);
        Assert.Contains(DashboardEventKind.AgentSessionCompleted, kinds);
    }

    [Fact]
    public async Task DispatchSingleTask_RunnerException_RetriesThenPermanentFailure()
    {
        // _maxRetryCount=1: first failure retries (Pending), second failure
        // permanently fails (Failed).
        var orch = BuildOrchestrator(new ThrowingRunner(new InvalidOperationException("boom")));
        BindMaf(orch);

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: DevTaskType, Title: "x", Description: "y"));

        var first = await orch.DispatchSingleTaskAsync(issue, CancellationToken.None);
        Assert.False(first.Success);
        Assert.Contains("boom", first.Message, StringComparison.OrdinalIgnoreCase);

        var afterFirst = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Pending, afterFirst.Status);
        Assert.Equal("1", afterFirst.GetMetadata("retryCount"));
        Assert.Contains("InvalidOperationException", afterFirst.GetMetadata("lastError") ?? "", StringComparison.OrdinalIgnoreCase);

        // Second dispatch: claim succeeds (status is Pending), runner throws
        // again, retryCount would be 1 → not < 1 → permanent Failed.
        var second = await orch.DispatchSingleTaskAsync(afterFirst, CancellationToken.None);
        Assert.False(second.Success);

        var afterSecond = (await _issues.GetAsync(issue.Id, CancellationToken.None))!;
        Assert.Equal(IssueStatus.Failed, afterSecond.Status);
    }

    private static void InitRepo(string dir)
    {
        RunGit(dir, "init -q -b main");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config user.name Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# init");
        RunGit(dir, "add -A");
        RunGit(dir, "commit -q -m initial");
    }

    private static void RunGit(string dir, string args)
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
        using var p = Process.Start(psi);
        p!.WaitForExit();
    }

    private sealed class ScriptedRunner : IAgentRunner
    {
        private readonly string _text;
        public ScriptedRunner(string text) { _text = text; }
public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, CancellationToken ct)
            => RunAsync(role, prompt, sessionId, context: null, ct);
        public Task<AgentRunResult> RunAsync(
            AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => Task.FromResult(new AgentRunResult(Text: _text, SessionId: null, InputTokens: 0, OutputTokens: 0, Elapsed: TimeSpan.Zero));
    }

    private sealed class CapturingRunner : IAgentRunner
    {
        private readonly string _text;
        public IReadOnlyDictionary<string, object>? LastContext { get; private set; }
        public string? LastPrompt { get; private set; }
        public CapturingRunner(string text) { _text = text; }
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, CancellationToken ct)
            => RunAsync(role, prompt, sessionId, context: null, ct);
        public Task<AgentRunResult> RunAsync(
            AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
        {
            LastContext = context;
            LastPrompt = prompt;
            return Task.FromResult(new AgentRunResult(Text: _text, SessionId: null, InputTokens: 0, OutputTokens: 0, Elapsed: TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class ThrowingRunner : IAgentRunner
    {
        private readonly Exception _ex;
        public ThrowingRunner(Exception ex) { _ex = ex; }
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, CancellationToken ct)
            => throw _ex;
        public Task<AgentRunResult> RunAsync(
            AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => throw _ex;
    }
}
