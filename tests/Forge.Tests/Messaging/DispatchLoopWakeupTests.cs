using System.Diagnostics;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Workflow;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Messaging;

/// <summary>
/// Dispatch-loop wakeup: with a <see cref="WakeupSignal"/> wired, a task
/// enqueued AFTER the loop's first cycle is claimed as soon as the
/// signal fires — the 15-minute backstop never gets to run. Proves the
/// poll-interval sleep is gone (without the signal the loop would stall
/// the whole test timeout).
/// </summary>
public sealed class DispatchLoopWakeupTests : IDisposable
{
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
    private readonly WakeupSignal _wakeup = new();

    public DispatchLoopWakeupTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("wakeup");
        _dataRoot = TempRoot.Instance.NewDirectory("wakeup-data");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_dataRoot);
        InitRepo(_workDir);

        _dbPath = Path.Combine(_dataRoot, "issues.db");
        _issues = new IssueStore(_dbPath,"test");
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
                Id = "test", Name = "Test", RepoUrl = "", DefaultBranch = "main", Root = _workDir,
            },
            issueStore: _issues,
            agents: new AgentStore(_issues),
            sprints: new SprintStore(_issues),
            designArtifacts: new DesignArtifactStore(_dbPath),
            artOutputs: new ArtOutputStore(_dbPath),
            worktrees: _worktrees,
            gitHub: _github,
            prWatcher: _prWatcher,
            events: _events,
            logger: NullLogger<ProjectDispatchBundle>.Instance);
    }

    private async Task ActivateApprovedAsync(IssueRecord task)
    {
        var scope=await ApprovedSprintFixture.Scope(_issues);await ApprovedSprintFixture.Bind(_issues,scope,[task]);
        var snapshot=await new SprintActivationValidator(_issues,_bundle.Sprints,new SpecStore(_issues),"test",null).ValidateAsync([task.Id],default);
        await _bundle.Sprints.CreateWithIssuesAsync(new("test","test",DateTime.UtcNow,DateTime.UtcNow.AddDays(1)),snapshot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        try { Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

    private sealed class ScriptedRunner(string response) : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => Task.FromResult(new AgentRunResult(response, null, 1, 1, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class DrainingRunner : IAgentRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
        {
            using var registration = ct.Register(() => CancellationObserved.TrySetResult());
            Started.TrySetResult();
            await AllowExit.Task;
            ct.ThrowIfCancellationRequested();
            return new AgentRunResult("NO_CHANGES_NEEDED", null, 1, 1, TimeSpan.Zero);
        }
    }

    private sealed class StubBundleFactory(ProjectDispatchBundle bundle) : IProjectDispatchBundleFactory
    {
        public ProjectDispatchBundle Build(ProjectOptions project) => bundle;
    }

    private OrchestratorAgent BuildOrchestrator(IAgentRunner runner)
    {
        var orch = new OrchestratorAgent(
            _projectStore,
            new StubBundleFactory(_bundle),
            runner,
            _roleRegistry,
            _messageBus,
            new InProcessDispatcher(
                (issue, bundle, ct) => RunWorkflowInProcess(runner, issue, ct),
                NullLogger<InProcessDispatcher>.Instance),
            _events,
            NullLogger<OrchestratorAgent>.Instance,
            wakeup: _wakeup);
        orch.BindOptions(new AgentOptions
        {
            Workspace = new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            Spawner = new SpawnerOptions { MaxConcurrentSessions = 1, PollIntervalSeconds = 3600 },
        });
        return orch;
    }

    private async Task RunWorkflowInProcess(IAgentRunner runner, IssueRecord issue, CancellationToken ct)
    {
        var workflow = new EngineeringDispatchWorkflow(
            issues: _issues,
            agentRunner: runner,
            worktrees: _worktrees,
            gitHub: _github,
            roleRegistry: _roleRegistry,
            workspaceOptions: new WorkspaceOptions
            {
                Root = _workDir, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main",
            },
            events: _events,
            drainMessageBus: agent => _messageBus.Drain(agent),
            designArtifacts: new DesignArtifactStore(_dbPath),
            artOutputs: new ArtOutputStore(_dbPath),
            memoryExtractor: new NoOpMemoryExtractor(),
            extractionStore: new MemoryExtractionStore(_dbPath),
            logger: NullLogger<EngineeringDispatchWorkflow>.Instance);
        await workflow.RunAsync(issue, ct);
    }

    [Fact]
    public async Task EnqueueAfterFirstCycle_SignalWakesLoop_TaskDispatches()
    {
        await _projectStore.UpsertAsync(new NewProject(
            Id: "test", Name: "Test", RepoUrl: _workDir, DefaultBranch: "main"));

        var orch = BuildOrchestrator(new ScriptedRunner("ok. NO_CHANGES_NEEDED"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = orch.ExecuteAsync(cts.Token);

        // The loop runs its first cycle immediately, then parks on the
        // wakeup signal (PollIntervalSeconds is deliberately huge —
        // unreachable in this test — so only the signal can wake it).
        // Give the first cycle a moment to park.
        await Task.Delay(500, cts.Token);

        var task = await _issues.CreateAsync(new NewIssue(
            Type: "dev", Title: "late work", Description: "enqueued after the loop parked",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await ActivateApprovedAsync(task);

        // The kick (production: TaskEnqueuedConsumer on the bus).
        _wakeup.Signal();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (loop.IsFaulted) Assert.Fail($"dispatch loop faulted: {loop.Exception?.GetBaseException()}");
            var t = await _issues.GetAsync(task.Id, cts.Token);
            if (t!.Status != IssueStatus.Pending) break;
            await Task.Delay(100, cts.Token);
        }

        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        var after = (await _issues.GetAsync(task.Id))!;
        Assert.NotEqual(IssueStatus.Pending, after.Status);
    }

    [Fact]
    public async Task Shutdown_DoesNotReturnUntilLocalDispatchHasDrained()
    {
        await _projectStore.UpsertAsync(new NewProject("test", "Test", _workDir, "main"));
        var task = await _issues.CreateAsync(new NewIssue(Type: "dev", Title: "drain work",
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));
        await ActivateApprovedAsync(task);
        var runner = new DrainingRunner();
        using var stop = new CancellationTokenSource();
        var loop = BuildOrchestrator(runner).ExecuteAsync(stop.Token);
        try
        {
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            stop.Cancel();
            await runner.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(loop.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            runner.AllowExit.TrySetResult();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static void InitRepo(string path)
    {
        RunGit(path, "init", "-q", "-b", "main");
        RunGit(path, "config", "user.email", "a@b");
        RunGit(path, "config", "user.name", "a");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test\n");
        RunGit(path, "add", "README.md");
        RunGit(path, "commit", "-q", "-m", "initial");
        var origin = Path.Combine(path, ".portHorizon", "fixture-origin.git");
        Directory.CreateDirectory(Path.GetDirectoryName(origin)!);
        RunGit(path, "init", "--bare", "-q", origin);
        RunGit(path, "remote", "add", "origin", origin);
        RunGit(path, "push", "-q", "-u", "origin", "main");
    }

    private static void RunGit(string cwd, string verb, params string[] args)
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
        foreach (var part in args) psi.ArgumentList.Add(part);
        using var p = Process.Start(psi)!;
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {verb} {args} failed: {p.StandardError.ReadToEnd()}");
    }
}
