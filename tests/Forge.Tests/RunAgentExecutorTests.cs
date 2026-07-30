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
            NullLogger<RunAgentExecutor>.Instance, projectId: null, sprints: null, default);

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
            NullLogger<RunAgentExecutor>.Instance, projectId: null, sprints: null, default);

        Assert.Equal(AgentResult.Skipped, result.Result);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task RunAgent_TimesOut_TransitionsToPending()
    {
        // Simulate an LLM call that never returns (hangs forever)
        // by using a chat client that blocks on a never-completing task.
        var hangingClient = new HangingChatClient();
        var factory = new TestScriptingFactory(hangingClient);
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-rae-md-{Guid.NewGuid():N}"));

        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        // Use a very short timeout (0.02 minutes ≈ 1.2s) so the test completes quickly.
        // The test uses CancellationToken.None for the outer CT so only
        // the timeout triggers.
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await RunAgentExecutor.HandleAsync(
                worktree, _issues, runner, _roleRegistry, _ => null, _events,
                new DesignArtifactStore(Path.Combine(_workDir, "issues.db")),
                new ArtOutputStore(Path.Combine(_workDir, "issues.db")),
                NullLogger<RunAgentExecutor>.Instance,
                projectId: null, sprints: null, CancellationToken.None,
                timeoutMinutes: 0.02);
        });

        // After the timeout, the issue should be Pending (for retry)
        // and metadata should contain the diagnostic entry.
        var after = await _issues.GetAsync(issue.Id);
        Assert.NotNull(after);
        Assert.Equal(IssueStatus.Pending, after.Status);
        var lastError = after.GetMetadata("lastError");
        Assert.Contains("timed out", lastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("true", after.GetMetadata("agentTimeout"));
        Assert.Contains("<timed out", after.GetMetadata("modelResponse"));
    }

    [Fact]
    public async Task RunAgent_FinishesWithinTimeout_IsUnaffected()
    {
        // Normal run with a scripted client that returns quickly
        // should complete normally even with a short timeout.
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
            NullLogger<RunAgentExecutor>.Instance,
            projectId: null, sprints: null, CancellationToken.None,
            timeoutMinutes: 1);

        Assert.Equal(AgentResult.Ok, result.Result);
        Assert.Contains("I made the change", result.Text);
        var after = await _issues.GetAsync(issue.Id);
        Assert.NotNull(after);
        Assert.Equal(IssueStatus.InProgress, after.Status);
        Assert.Null(after.GetMetadata("agentTimeout"));
    }

    [Fact]
    public async Task RunAgent_ReworkResume_PromptNotesSyncedHead()
    {
        // Pause/resume honesty: a rework round resumes the dev's
        // persisted session, so the prompt must tell the agent the
        // worktree moved under it (re-read files before editing).
        var issue = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "x",
            Metadata: new Dictionary<string, object>
            {
                ["reworkForSha"] = "abc1234567890",
                ["reworkContext"] = "CI failed: build error",
            }));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        var capturing = new CapturingChatClient();
        var runner = new MafAgentRunner(
            chatClientFactory: new TestScriptingFactory(capturing),
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-rae-md-{Guid.NewGuid():N}"));
        var result = await RunAgentExecutor.HandleAsync(
            worktree, _issues, runner, _roleRegistry, _ => null, _events,
            new DesignArtifactStore(Path.Combine(_workDir, "issues.db")),
            new ArtOutputStore(Path.Combine(_workDir, "issues.db")),
            NullLogger<RunAgentExecutor>.Instance, projectId: null, sprints: null, default);

        Assert.Equal(AgentResult.Ok, result.Result);
        var userText = string.Join("\n", capturing.Messages.Select(m => m.Text));
        Assert.Contains("Resumed session", userText);
        Assert.Contains("re-read any file", userText);
        Assert.Contains("abc1234", userText);   // short sha of the synced head
    }

    /// <summary>Records the incoming messages of the first call and
    /// answers with fixed text.</summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = Array.Empty<ChatMessage>();
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Test-only IChatClient that never returns from GetResponseAsync
    /// until the CancellationToken is cancelled. Used to simulate a
    /// hanging LLM call for timeout tests.
    /// </summary>
    private sealed class HangingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Wait until cancelled or forever
            var tcs = new TaskCompletionSource<ChatResponse>();
            await using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task; // throws OperationCanceledException on cancel
            throw new InvalidOperationException("Should never reach here");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}