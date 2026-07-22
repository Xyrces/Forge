using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator.Workflow;
using Forge.Tests.Integration.TestHelpers;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P3 checkpoint 4: RunAgentExecutor drives the agent via
/// IAgentRunner.RunAsync. Tests use a real temp git repo for the
/// worktree stage and a scripted chat client so the agent
/// invocation is deterministic and offline.
/// </summary>
public class RunAgentExecutorTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _worktrees;
    private readonly InMemoryDashboardEventBus _events;
    private readonly RoleAgentRegistry _roleRegistry = new();

    public RunAgentExecutorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-rae-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db"));
        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email test@test", dir);
        Run("git", "config user.name Test", dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add README.md", dir);
        Run("git", "commit -q -m init", dir);
    }

    private static void Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static IAgentRunner RunnerWithScriptedClient()
    {
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I made the change.")));
        var factory = new TestScriptingFactory(scripted);
        return new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-rae-md-{Guid.NewGuid():N}"));
    }

    private sealed class TestScriptingFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public TestScriptingFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    [Fact]
    public async Task RunAgent_OkCapturesModelText()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        var runner = RunnerWithScriptedClient();
        var result = await RunAgentExecutor.HandleAsync(
            worktree, _issues, runner, _roleRegistry, _ => null, _events,
            new DesignArtifactStore(Path.Combine(_workDir, "issues.db")),
            new ArtOutputStore(Path.Combine(_workDir, "issues.db")),
            NullLogger<RunAgentExecutor>.Instance, projectId: null, default);

        Assert.Equal(AgentResult.Ok, result.Result);
        Assert.Contains("I made the change", result.Text);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public async Task RunAgent_SkippedForAlreadyClaimed()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        // Force AlreadyClaimed on the second claim attempt.
        var claimedDup = await ClaimExecutor.HandleAsync(
            claimed.Issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        // Manually construct a WorktreeReady in AlreadyClaimed state
        // (the worktree executor never sees AlreadyClaimed in normal
        // flow; we test the guard directly).
        var worktreeSkipped = new WorktreeReady(claimedDup, WorktreeResult.AlreadyClaimed, null, "main");

        var runner = RunnerWithScriptedClient();
        var result = await RunAgentExecutor.HandleAsync(
            worktreeSkipped, _issues, runner, _roleRegistry, _ => null, _events,
            new DesignArtifactStore(Path.Combine(_workDir, "issues.db")),
            new ArtOutputStore(Path.Combine(_workDir, "issues.db")),
            NullLogger<RunAgentExecutor>.Instance, projectId: null, default);

        Assert.Equal(AgentResult.Skipped, result.Result);
        Assert.Equal(string.Empty, result.Text);
    }
}