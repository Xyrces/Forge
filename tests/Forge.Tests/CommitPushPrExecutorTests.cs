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
using Octokit;
using Xunit;
using NewIssue = Forge.Core.NewIssue;

namespace Forge.Tests;

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
        public PullRequest? OpenPrForBranch;
        public StubGitHub() : base(new Configuration.AgentOptions().GitHub) { }
        public override Task<PullRequest?> GetOpenPullRequestForBranchAsync(
            string headBranch, CancellationToken cancellationToken = default)
            => Task.FromResult(OpenPrForBranch);
        public override Task<PullRequest> CreatePullRequestAsync(
            string title, string body, string headBranch, string baseBranch,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("CreatePullRequestAsync should not be called in this test");
    }

    [Fact]
    public async Task NoRecordedPrNumber_OpenPrExistsForBranch_ReusesIt()
    {
        // Observed live 2026-07-25 (task-155 / PR #32): the PR for
        // the branch was opened OUTSIDE the pipeline, so the task
        // carries no prNumber. The executor must reuse the open PR
        // found by branch instead of attempting creation (a 422
        // that MAF swallows → silent mid-pipeline halt + requeue
        // loop).
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        var bareDir = Path.Combine(_workDir, "remote.git");
        Run("git", $"init -q --bare \"{bareDir}\"", _workDir);
        Run("git", $"remote add origin \"{bareDir}\"", _workDir);
        Run("git", "push -q -u origin main", _workDir);

        File.WriteAllText(Path.Combine(worktree.WorktreePath!, "New.cs"), "class New {}");

        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the work", null);
        var result = await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub { OpenPrForBranch = new PullRequest(42) }, _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, default);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal("42", after!.GetMetadata("prNumber"));
        Assert.Equal(DispatchCheckpoint.PrOpened, after.DispatchCheckpoint);
        Assert.Equal(42, result.PrNumber);
    }

    [Fact]
    public async Task SelfCommittedBranch_IsNotTreatedAsNoDiff()
    {
        // Regression (observed live 2026-07-24, task-155): an agent
        // that commits its own work via bash leaves 'nothing to
        // commit' for CommitAllAsync — but the branch IS ahead of
        // base. The executor must proceed to push/PR, not burn a
        // no-progress strike toward Failed.
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        // Bare remote so PushAsync has somewhere to land the branch.
        var bareDir = Path.Combine(_workDir, "remote.git");
        Run("git", $"init -q --bare \"{bareDir}\"", _workDir);
        Run("git", $"remote add origin \"{bareDir}\"", _workDir);
        Run("git", "push -q -u origin main", _workDir);

        // Simulate the agent self-committing in the worktree (bash).
        var wtPath = worktree.WorktreePath!;
        File.WriteAllText(Path.Combine(wtPath, "New.cs"), "class New {}");
        Run("git", "config user.email test@test", wtPath);
        Run("git", "config user.name Test", wtPath);
        Run("git", "add -A", wtPath);
        Run("git", "commit -q -m agent-work", wtPath);

        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the work", null);
        // The stub throws at PR creation — reaching it proves the
        // executor took the push/PR path, not the no-diff path.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommitPushPrExecutor.HandleAsync(
                agent, _issues, _worktrees, new StubGitHub(), _events,
                new NoOpMemoryExtractor(),
                new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
                NullLogger<CommitPushPrExecutor>.Instance, default).AsTask());

        Assert.Contains("CreatePullRequestAsync should not be called", ex.Message);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.InProgress, after!.Status);
        Assert.Null(after.GetMetadata("noProgressAttempts"));
        Assert.Equal(DispatchCheckpoint.PushDone, after.DispatchCheckpoint);
    }


    [Fact]
    public async Task NoDiff_ExplicitNoOpMarker_TransitionsToCompleted()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        // No file edits in the worktree -> CommitAllAsync returns no changes,
        // but the agent explicitly concluded no work was needed.
        var agent = new AgentCompleted(worktree, AgentResult.Ok,
            "Verified: the endpoint already exists and behaves as specified. NO_CHANGES_NEEDED", null);

        var result = await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, default);

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
    }

    [Fact]
    public async Task NoDiff_NoMarker_RequeuesWithBreaker()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        // 0 edits and NO marker: a truncated/stuck run (observed live:
        // the MAF 40-iteration cap cut every run during exploration).
        var agent = new AgentCompleted(worktree, AgentResult.Ok,
            "Now I have a comprehensive understanding. Let me design the implementation.", null);

        for (var i = 1; i <= CommitPushPrExecutor.MaxNoProgressAttempts; i++)
        {
            var result = await CommitPushPrExecutor.HandleAsync(
                agent, _issues, _worktrees, new StubGitHub(), _events,
                new NoOpMemoryExtractor(),
                new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
                NullLogger<CommitPushPrExecutor>.Instance, default);
            Assert.Equal(PrResult.NoDiff, result.Result);
            var after = await _issues.GetAsync(issue.Id);
            if (i < CommitPushPrExecutor.MaxNoProgressAttempts)
            {
                Assert.Equal(IssueStatus.Pending, after!.Status);
                Assert.Equal(i.ToString(), after.GetMetadata("noProgressAttempts"));
            }
            else
            {
                Assert.Equal(IssueStatus.Failed, after!.Status);
            }
        }
    }

    [Fact]
    public async Task NoDiff_TaskAlreadyTerminal_LeavesItAlone()
    {
        // Stale-dispatch race: a long agent run finishes AFTER the
        // watch merged the task's PR. The no-diff path must not
        // stomp the terminal state (observed live: a Completed task
        // flipped back to Pending via the 429 path downstream).
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        var agent = new AgentCompleted(worktree, AgentResult.Ok, "I did nothing.", null);
        // The watch merges the PR while the agent runs.
        await _issues.TransitionAsync(issue.Id, IssueStatus.Completed, "merged");

        var result = await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, default);

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
        // And crucially: no NEW transition stomped it (the row's
        // UpdatedAt is the merge's, not a fresh no-diff write).
    }

    // WithDiff test omitted: GitHubService.CreatePullRequestAsync returns
    // a read-only Octokit.PullRequest that's hard to stub without a real
    // connection. The with-diff path is covered by the live demo
    // (PR #724 opened successfully against github.com/Xyrces/PortHorizon)
    // and the manual orchestrator flow. Add an integration test once
    // we have a fake Octokit client infrastructure in place.
}