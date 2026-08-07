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
        _workDir = TempRoot.Instance.NewDirectory("cppr");
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
            NullLogger<CommitPushPrExecutor>.Instance, null, default);

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
                NullLogger<CommitPushPrExecutor>.Instance, null, default).AsTask());

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
            NullLogger<CommitPushPrExecutor>.Instance, null, default);

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
    }

    [Fact]
    public async Task NoDiff_ExplicitMarker_PolicyRework_RequeuesInsteadOfCompleting()
    {
        // Workflow policy noDiffOutcome=rework (pass 3): the operator
        // doesn't accept verified no-op completions — the task
        // requeues (no-progress breaker still caps the loop).
        var memory = new Forge.Core.MemoryStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db"));
        var def = Forge.Core.Workflow.WorkflowDefaults.Definition with
        {
            Policies = new Dictionary<string, string>(Forge.Core.Workflow.WorkflowDefaults.Definition.Policies)
            {
                [Forge.Core.Workflow.WorkflowPolicies.NoDiffOutcome] = "rework",
            },
        };
        await memory.RememberAsync(Forge.Core.Workflow.WorkflowResolver.LiveKey,
            Forge.Core.Workflow.WorkflowResolver.Serialize(def));
        var resolver = new Forge.Core.Workflow.WorkflowResolver(memory);

        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        var agent = new AgentCompleted(worktree, AgentResult.Ok,
            "Verified: nothing to do. NO_CHANGES_NEEDED", null);

        var result = await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, resolver, default);

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Equal("1", after.GetMetadata("noProgressAttempts"));
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
                NullLogger<CommitPushPrExecutor>.Instance, null, default);
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
    public async Task VerifyFailure_BounceSeedsReworkContext()
    {
        // Operator rule 2026-07-31: a failed build returns to the run
        // CONTEXT to be fixed — the bounce must seed the
        // reworkReason/reworkContext the next round's prompt renders.
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        var wtPath = worktree.WorktreePath!;
        Run("git", "config user.email test@test", wtPath);
        Run("git", "config user.name Test", wtPath);
        File.WriteAllText(Path.Combine(wtPath, "New.cs"), "class New {}");
        Run("git", "add -A", wtPath);
        Run("git", "commit -q -m agent-work", wtPath);

        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the work", null);
        await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, null,
            verifyCommands: new[] { "exit 1" }, ct: default);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Equal("1", after.GetMetadata("noProgressAttempts"));
        Assert.Equal("pre-push verification failed (attempt 1)", after.GetMetadata("reworkReason"));
        var ctx = after.GetMetadata("reworkContext");
        Assert.Contains("FAILED the pre-push build/test verification", ctx);
    }

    [Fact]
    public async Task ReworkBranchDiverged_BouncesWithGuidance_NoPush()
    {
        // Live 2026-08-01 (task-377): the agent reset the rework
        // branch onto main mid-round — the PR head is no longer an
        // ancestor, the push is a non-fast-forward rejection, and
        // before the guard that throw vanished into MAF's silent
        // halt while the stall guard burned strikes. The executor
        // must bounce with explicit "build on the PR branch"
        // guidance instead.
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x",
            Metadata: new Dictionary<string, object> { ["prNumber"] = "775" }));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        var bareDir = Path.Combine(_workDir, "remote.git");
        Run("git", $"init -q --bare \"{bareDir}\"", _workDir);
        Run("git", $"remote add origin \"{bareDir}\"", _workDir);
        Run("git", "push -q -u origin main", _workDir);
        var wtPath = worktree.WorktreePath!;
        Run("git", "config user.email test@test", wtPath);
        Run("git", "config user.name Test", wtPath);
        File.WriteAllText(Path.Combine(wtPath, "New.cs"), "class New {}");
        Run("git", "add -A", wtPath);
        Run("git", "commit -q -m agent-work", wtPath);

        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the work", null);
        await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, null,
            verifyCommands: Array.Empty<string>(), ct: default);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Equal("1", after.GetMetadata("noProgressAttempts"));
        Assert.Equal("rework branch diverged from PR head (attempt 1)", after.GetMetadata("reworkReason"));
        Assert.Contains("Do NOT reset or rebase", after.GetMetadata("reworkContext"));
    }

    [Fact]
    public async Task PushSuccess_ClearsNoProgressCounterAndBounceContext()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        var bareDir = Path.Combine(_workDir, "remote.git");
        Run("git", $"init -q --bare \"{bareDir}\"", _workDir);
        Run("git", $"remote add origin \"{bareDir}\"", _workDir);
        Run("git", "push -q -u origin main", _workDir);
        var wtPath = worktree.WorktreePath!;
        Run("git", "config user.email test@test", wtPath);
        Run("git", "config user.name Test", wtPath);
        File.WriteAllText(Path.Combine(wtPath, "New.cs"), "class New {}");
        Run("git", "add -A", wtPath);
        Run("git", "commit -q -m agent-work", wtPath);
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object>
            {
                ["noProgressAttempts"] = "1",
                ["reworkReason"] = "pre-push verification failed (attempt 1)",
                ["reworkContext"] = "old failure output",
            });

        var agent = new AgentCompleted(worktree, AgentResult.Ok, "did the work", null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommitPushPrExecutor.HandleAsync(
                agent, _issues, _worktrees, new StubGitHub(), _events,
                new NoOpMemoryExtractor(),
                new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
                NullLogger<CommitPushPrExecutor>.Instance, null,
                verifyCommands: Array.Empty<string>(), ct: default).AsTask());

        Assert.Contains("CreatePullRequestAsync should not be called", ex.Message);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Null(after!.GetMetadata("noProgressAttempts"));
        Assert.Null(after.GetMetadata("reworkReason"));
        Assert.Null(after.GetMetadata("reworkContext"));
    }

    [Fact]
    public async Task NoDiff_BounceSeedsReworkContext()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var worktree = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        var agent = new AgentCompleted(worktree, AgentResult.Ok,
            "Now I have a comprehensive understanding.", null);

        await CommitPushPrExecutor.HandleAsync(
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, null, default);

        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Contains("no diff", after.GetMetadata("reworkReason"));
        Assert.Contains("produced NO changes", after.GetMetadata("reworkContext"));
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
            NullLogger<CommitPushPrExecutor>.Instance, null, default);

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Completed, after!.Status);
        // And crucially: no NEW transition stomped it (the row's
        // UpdatedAt is the merge's, not a fresh no-diff write).
    }

    [Fact]
    public async Task VerificationFailure_RequeuesWithOutput_NoPush()
    {
        // The pre-push gate: a failing build/test bounces the task
        // back to the agent with the output — no push, no PR, no
        // watch round. GitHub CI stays the safety net.
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
            agent, _issues, _worktrees, new StubGitHub(), _events,
            new NoOpMemoryExtractor(),
            new MemoryExtractionStore(Path.Combine(_workDir, "extraction.db")),
            NullLogger<CommitPushPrExecutor>.Instance, null,
            verifyCommands: new[] { "echo BUILD-BROKE-MARKER; exit 1" });

        Assert.Equal(PrResult.NoDiff, result.Result);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Equal("1", after.GetMetadata("noProgressAttempts"));
        Assert.Contains("BUILD-BROKE-MARKER", after.GetMetadata("lastError"));
        // No push: the push checkpoint was never reached.
        Assert.True(after.DispatchCheckpoint < DispatchCheckpoint.PushDone,
            $"expected no push, checkpoint is {after.DispatchCheckpoint}");
    }

    [Fact]
    public async Task VerificationPass_ProceedsToPush()
    {
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
            NullLogger<CommitPushPrExecutor>.Instance, null,
            verifyCommands: new[] { "true" });

        Assert.Equal(42, result.PrNumber);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(DispatchCheckpoint.PrOpened, after!.DispatchCheckpoint);
    }

    [Fact]
    public void DefaultVerification_DotnetRepo_BuildsAndTests()
    {
        File.WriteAllText(Path.Combine(_workDir, "X.csproj"), "<Project />");
        var commands = AgentTools.RunVerification.DefaultCommands(_workDir);
        Assert.Equal(2, commands.Count);
        Assert.Contains("build", commands[0]);
        Assert.Contains("test", commands[1]);
    }

    [Fact]
    public void DefaultVerification_NonDotnetRepo_NoCommands()
    {
        var empty = Path.Combine(_workDir, "empty");
        Directory.CreateDirectory(empty);
        Assert.Empty(AgentTools.RunVerification.DefaultCommands(empty));
    }

    // WithDiff test omitted: GitHubService.CreatePullRequestAsync returns
    // a read-only Octokit.PullRequest that's hard to stub without a real
    // connection. The with-diff path is covered by the live demo
    // (PR #724 opened successfully against github.com/Xyrces/PortHorizon)
    // and the manual orchestrator flow. Add an integration test once
    // we have a fake Octokit client infrastructure in place.
}