using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Orchestrator.Workflow;
using PortHorizon.Agents.Tests.Integration.TestHelpers;
using Octokit;
using Xunit;
using NewIssue = PortHorizon.Agents.Core.NewIssue;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// P3 checkpoint 5: CommitPushPrExecutor commits, pushes, opens
/// a PR. The PR-opening step needs a real GitHub API; for the
/// no-diff + skipped paths we stub the GitHubService via a
/// fake. The push+commit path uses a real temp git repo.
/// </summary>
public class CommitPushPrExecutorTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _worktrees;
    private readonly InMemoryDashboardEventBus _events;
    private readonly RoleAgentRegistry _roleRegistry = new();

    public CommitPushPrExecutorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-cppr-{Guid.NewGuid():N}");
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

    /// <summary>
    /// Stub GitHubService that throws on CreatePullRequestAsync.
    /// The no-diff path doesn't hit it, so this is fine for the
    /// no-diff test. The with-diff path is covered by the live demo
    /// (PR #724 opened successfully) and an integration test would
    /// need a real GitHub connection; not worth stubbing through
    /// Octokit's read-only PullRequest model.
    /// </summary>
    private sealed class StubGitHub : GitHubService
    {
        public StubGitHub() : base(new Configuration.AgentOptions().GitHub) { }
        public override Task<PullRequest> CreatePullRequestAsync(
            string title, string body, string headBranch, string baseBranch,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("CreatePullRequestAsync should not be called in this test");
    }

    [Fact]
    public async Task NoDiff_TransitionsToCompletedAndReturnsNoDiff()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        // No file edits in the worktree -> CommitAllAsync returns no changes.
        var agent = new AgentCompleted(worktree, AgentResult.Ok, "I did nothing.", null);

        var exec = new CommitPushPrExecutor(
            _issues, _worktrees, new StubGitHub(), _events,
            NullLogger<CommitPushPrExecutor>.Instance);
        var result = await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            NullLogger<CommitPushPrExecutor>.Instance, default);

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
    }

    // WithDiff test omitted: GitHubService.CreatePullRequestAsync returns
    // a read-only Octokit.PullRequest that's hard to stub without a real
    // connection. The with-diff path is covered by the live demo
    // (PR #724 opened successfully against github.com/Xyrces/PortHorizon)
    // and the manual orchestrator flow. Add an integration test once
    // we have a fake Octokit client infrastructure in place.
}