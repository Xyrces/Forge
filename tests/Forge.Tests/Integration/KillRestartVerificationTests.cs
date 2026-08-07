using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Xunit;
using Xunit.Abstractions;
using NewIssue = Forge.Core.NewIssue;

namespace Forge.Tests.Integration;

/// <summary>
/// P4 Stage A.5 — kill + restart verification.
///
/// <para>
/// Simulates a crash mid-workflow: the orchestrator's
/// engineering dispatch workflow gets the LLM run done
/// (commit on the branch) and then the process dies
/// (SIGKILL, power loss, anything). The
/// <c>StartupRecovery</c> pass on the next launch should
/// replay the unfinished side-effects: push the branch
/// and open the PR.
/// </para>
///
/// <para>
/// This test uses a real git worktree + a real local bare
/// remote for the push. The PR creation is stubbed at the
/// <see cref="IGitHubRecovery"/> boundary; in production the
/// recoverer calls the real <see cref="GitHubService"/>.
/// </para>
/// </summary>
public class KillRestartVerificationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly RecoveryReportStore _reports;
    private readonly GitWorktreeService _worktrees;
    private readonly InMemoryDashboardEventBus _events;
    private readonly RecordingGitHub _gitHub;
    private readonly StartupRecovery _recovery;

    public KillRestartVerificationTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = TempRoot.Instance.NewDirectory("killrestart");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _ = new IssueStore(_dbPath);
        _issues = new IssueStore(_dbPath);
        _reports = new RecoveryReportStore(_dbPath);
        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        _events = new InMemoryDashboardEventBus();
        _gitHub = new RecordingGitHub();
        _recovery = new StartupRecovery(_issues, _reports, _worktrees, _gitHub, _events,
            NullLogger<StartupRecovery>.Instance);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    /// <summary>Stub GitHub that records calls + returns a fixed PR.</summary>
    private sealed class RecordingGitHub : IGitHubRecovery
    {
        public List<(string Title, string Head, string Base)> Calls = new();
        public int NextPrNumber = 1234;
        public Task<PullRequest> CreatePullRequestAsync(
            string title, string body, string headBranch, string baseBranch,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((title, headBranch, baseBranch));
            return Task.FromResult(new PullRequest(NextPrNumber));
        }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email t@t", dir);
        Run("git", "config user.name T", dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add .", dir);
        Run("git", "commit -q -m init", dir);
        var bare = Path.Combine(dir, ".remote.git");
        Run("git", $"init --bare -q {bare}", dir);
        Run("git", $"remote add origin {bare}", dir);
        Run("git", "push -q -u origin main", dir);
    }

    private static void Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    /// <summary>
    /// Simulate: orchestrator dispatched task-1, the LLM ran,
    /// files were written to the worktree, a commit was made
    /// on the local branch, then the process died before push
    /// or PR-open. Recovery should: detect the commit_done
    /// state, push the branch, open the PR, and update the
    /// issue's checkpoint to pr_opened.
    /// </summary>
    [Fact]
    public async Task KillAfterCommitBeforePush_RecoveryPushesAndOpensPr()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Inventory HUD"));
        await _issues.ClaimAsync(issue.Id, "forge");
        await _worktrees.CreateAsync(issue.Id, "main");
        var worktreePath = _worktrees.WorktreePathFor(issue.Id);
        var branch = $"agent/{issue.Id}";

        // Phase 1: worktree executor runs.
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.WorktreeAcquired);
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = branch,
            });

        // Phase 2: agent runs + writes files (simulated).
        File.WriteAllText(Path.Combine(worktreePath, "edit.txt"), "agent output");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted);

        // Phase 3: commit happens. <-- simulate the crash here.
        var commit = await _worktrees.CommitAllAsync(worktreePath, $"Task({issue.Id}): Inventory HUD");
        Assert.True(commit.HasChanges);
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone);

        // Sanity: the branch exists locally but hasn't been pushed.
        var branchExistsRemotely = await CheckRemoteBranchAsync(_workDir, branch);
        Assert.False(branchExistsRemotely, "branch should not be on the remote before recovery");

        // *** CRASH *** — no push, no PR. We don't transition the
        // issue further; the DB row sits at InProgress +
        // commit_done + (no prNumber).

        // Phase 4: orchestrator restarts. StartupRecovery runs.
        var reportId = await _recovery.RunAsync();
        var report = (await _reports.GetAsync(reportId))!;
        _out.WriteLine($"Report: scanned={report.IssuesScanned} replayed={report.IssuesReplayed} failed={report.IssuesFailed}");

        Assert.Equal(1, report.IssuesScanned);
        Assert.Equal(1, report.IssuesReplayed);
        Assert.Equal(0, report.IssuesFailed);

        // Verify the side-effects ran:
        Assert.True(await CheckRemoteBranchAsync(_workDir, branch), "branch should be on remote after recovery");
        Assert.Single(_gitHub.Calls);
        Assert.Equal(branch, _gitHub.Calls[0].Head);
        Assert.Equal("main", _gitHub.Calls[0].Base);
        Assert.Contains("Inventory HUD", _gitHub.Calls[0].Title);

        // Verify the issue's state advanced to pr_opened + prNumber recorded.
        var after = (await _issues.GetAsync(issue.Id))!;
        Assert.Equal(DispatchCheckpoint.PrOpened, after.DispatchCheckpoint);
        Assert.Equal(1234, int.Parse(after.GetMetadata("prNumber")!));
        Assert.Equal(1, after.RecoveryAttempts);
    }

    /// <summary>
    /// Simulate: orchestrator pushed the branch but crashed
    /// before opening the PR. Recovery should: open the PR
    /// using the already-pushed branch (no second push
    /// needed). Git is idempotent on the push so a no-op
    /// push is also acceptable; the contract is "PR is
    /// opened with the right head branch".
    /// </summary>
    [Fact]
    public async Task KillAfterPushBeforePr_RecoveryOpensPrWithoutPushingAgain()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Wiring"));
        await _issues.ClaimAsync(issue.Id, "forge");
        await _worktrees.CreateAsync(issue.Id, "main");
        var worktreePath = _worktrees.WorktreePathFor(issue.Id);
        var branch = $"agent/{issue.Id}";

        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.WorktreeAcquired);
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = branch,
            });
        File.WriteAllText(Path.Combine(worktreePath, "edit.txt"), "agent output");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted);
        var commit = await _worktrees.CommitAllAsync(worktreePath, $"Task({issue.Id}): Wiring");
        Assert.True(commit.HasChanges);
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone);

        // Push happens.
        await _worktrees.PushAsync(worktreePath, branch);
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone);

        // *** CRASH *** before PR-open.

        // Recover.
        var reportId = await _recovery.RunAsync();
        var report = (await _reports.GetAsync(reportId))!;
        Assert.Equal(1, report.IssuesReplayed);
        Assert.Equal(1, _gitHub.Calls.Count);

        var after = (await _issues.GetAsync(issue.Id))!;
        Assert.Equal(DispatchCheckpoint.PrOpened, after.DispatchCheckpoint);
        Assert.Equal(1234, int.Parse(after.GetMetadata("prNumber")!));
    }

    /// <summary>
    /// Orchestrator crashes BEFORE creating a worktree (only claimed
    /// + assignee=forge). At boot there are no live runs, so the
    /// claim is orphaned — and the dispatch loop only claims Pending,
    /// so "leave it alone" strands the task until the 30-min reaper
    /// (observed live 2026-07-31: task-18). The recoverer re-queues
    /// to Pending immediately; the next tick re-claims and
    /// re-creates the worktree idempotently.
    /// </summary>
    [Fact]
    public async Task KillAfterClaimBeforeWorktree_RecoveryRequeues()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Sketch"));
        await _issues.ClaimAsync(issue.Id, "forge");
        // No worktree created. No checkpoint advanced past "claimed".
        Assert.Equal(DispatchCheckpoint.Claimed, (await _issues.GetAsync(issue.Id))!.DispatchCheckpoint);

        var reportId = await _recovery.RunAsync();
        var report = (await _reports.GetAsync(reportId))!;
        Assert.Equal(1, report.IssuesScanned);
        Assert.Equal(0, report.IssuesReplayed);
        Assert.Equal(0, report.IssuesFailed);
        Assert.Equal(0, _gitHub.Calls.Count);

        // Re-queued to Pending: the dispatch loop re-claims it on the
        // next tick. Checkpoint unchanged — re-dispatch starts clean.
        var after = (await _issues.GetAsync(issue.Id))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Equal(DispatchCheckpoint.Claimed, after.DispatchCheckpoint);
    }

    /// <summary>Helper: is <paramref name="branch"/> on the remote (origin)?</summary>
    private static async Task<bool> CheckRemoteBranchAsync(string workDir, string branch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-remote --heads origin " + branch,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        var output = await p.StandardOutput.ReadToEndAsync();
        return !string.IsNullOrWhiteSpace(output);
    }
}