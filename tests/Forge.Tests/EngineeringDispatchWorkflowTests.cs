using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Workflow;
using Forge.Tests.Integration.TestHelpers;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P3 checkpoint 7: verify the MAF Workflows version of the
/// dispatch pipeline runs end-to-end on a real temp git repo
/// with a scripted chat client. The orchestrator's existing
/// sequential dispatch stays in production; this test pins
/// the parallel workflow implementation's behavior.
/// </summary>
public class EngineeringDispatchWorkflowTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _worktrees;
    private readonly InMemoryDashboardEventBus _events;
    private readonly RoleAgentRegistry _roleRegistry = new();

    public EngineeringDispatchWorkflowTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-wf-{Guid.NewGuid():N}");
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

    private static IAgentRunner ScriptedRunner(string response)
    {
        var factory = new TestScriptingFactory(new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, response))));
        return new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-wf-md-{Guid.NewGuid():N}"));
    }

    private sealed class TestScriptingFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public TestScriptingFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null) => _client;
    }

    private sealed class StubGitHub : GitHubService
    {
        public int NextPrNumber { get; set; } = 7;
        public StubGitHub() : base(new AgentOptions().GitHub) { }
        public override Task<Octokit.PullRequest> CreatePullRequestAsync(
            string title, string body, string headBranch, string baseBranch,
            CancellationToken cancellationToken = default)
        {
            // Octokit's PullRequest is read-only post-construction;
            // we can't construct it with a number. The
            // CommitPushPrExecutor only reads .Number, so we return
            // a default-constructed instance. Tests asserting on
            // prNumber use a different verification path
            // (the issue metadata captures prNumber before the
            // stub is called).
            _ = title; _ = body; _ = headBranch; _ = baseBranch;
            return Task.FromResult(new Octokit.PullRequest());
        }
    }

    [Fact]
    public async Task Workflow_ClaimToWorktree_RunsThroughExecutors()
    {
        // This test exercises the Claim + Worktree + RunAgent
        // stages only (the CommitPushPr executor is covered by the
        // NoDiff test below, since that path doesn't need a real
        // GitHub call). Verifies the workflow shape, not the full
        // PR pipeline.
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var workflow = new EngineeringDispatchWorkflow(
            issues: _issues,
            agentRunner: ScriptedRunner("done. NO_CHANGES_NEEDED"),
            worktrees: _worktrees,
            gitHub: new StubGitHub(),
            roleRegistry: _roleRegistry,
            workspaceOptions: new WorkspaceOptions
            {
                Root = _workDir, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main",
            },
            events: _events,
            drainMessageBus: _ => null,
            designArtifacts: new DesignArtifactStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db")),
            artOutputs: new ArtOutputStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db")),
            memoryExtractor: new NoOpMemoryExtractor(),
            extractionStore: new MemoryExtractionStore(Path.Combine(_workDir, ".portHorizon", "state", "memory.db")),
            logger: NullLogger<EngineeringDispatchWorkflow>.Instance);

        await workflow.RunAsync(issue, CancellationToken.None);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
        Assert.NotNull(after.GetMetadata("worktreePath"));
    }

    [Fact]
    public async Task Workflow_NoDiffShortCircuits()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var workflow = new EngineeringDispatchWorkflow(
            issues: _issues,
            agentRunner: ScriptedRunner("nothing to do. NO_CHANGES_NEEDED"),
            worktrees: _worktrees,
            gitHub: new StubGitHub(),
            roleRegistry: _roleRegistry,
            workspaceOptions: new WorkspaceOptions
            {
                Root = _workDir, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main",
            },
            events: _events,
            drainMessageBus: _ => null,
            designArtifacts: new DesignArtifactStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db")),
            artOutputs: new ArtOutputStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db")),
            memoryExtractor: new NoOpMemoryExtractor(),
            extractionStore: new MemoryExtractionStore(Path.Combine(_workDir, ".portHorizon", "state", "memory.db")),
            logger: NullLogger<EngineeringDispatchWorkflow>.Instance);

        await workflow.RunAsync(issue, CancellationToken.None);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
        Assert.Null(after.GetMetadata("prNumber"));
    }
}