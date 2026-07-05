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
using PullRequest = Octokit.PullRequest;

namespace Forge.Tests;

/// <summary>
/// P4 Stage A.2 — StartupRecovery.Classify / Replay / RunAsync.
/// See docs/p4-restart-safety.md. The GitHubService is stubbed
/// so the tests don't hit the real API; the git worktree
/// service runs against a real temp git repo.
/// </summary>
public class StartupRecoveryTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly RecoveryReportStore _reports;
    private readonly GitWorktreeService _worktrees;
    private readonly InMemoryDashboardEventBus _events;
    private readonly StubGitHub _gitHub;
    private readonly StartupRecovery _recovery;

    public StartupRecoveryTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-recovery-{Guid.NewGuid():N}");
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
        _gitHub = new StubGitHub();
        _recovery = new StartupRecovery(
            _issues, _reports, _worktrees, _gitHub, _events,
            NullLogger<StartupRecovery>.Instance);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email t@t", dir);
        Run("git", "config user.name T", dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add .", dir);
        Run("git", "commit -q -m init", dir);
        // Create a local "remote" so git push succeeds in tests.
        // We use a bare repo at <dir>/.remote.git and add it
        // as the origin. The recovery tests exercise the full
        // push -> PR open chain.
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

    /// <summary>Stub GitHub that records calls + returns a fixed PR.</summary>
    private sealed class StubGitHub : IGitHubRecovery
    {
        public int CallCount;
        public int PrNumber = 999;
        public Task<PullRequest> CreatePullRequestAsync(
            string title, string body, string headBranch, string baseBranch,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            // Octokit.PullRequest.Number has a private setter; only
            // the (int number) constructor sets it. Round-tripping
            // through JSON doesn't populate Number (no public /
            // init setter on init-only private props), so we use
            // the constructor directly.
            return Task.FromResult(new PullRequest(PrNumber));
        }
    }

    /// <summary>
    /// Build a real issue + worktree in the temp git repo, then
    /// advance the checkpoint to the requested state. Returns the issue id.
    /// </summary>
    private async Task<string> SeedIssueAsync(
        DispatchCheckpoint target, bool commitChanges = false, bool withPrNumber = false)
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await _issues.ClaimAsync(issue.Id, "kilo");
        Assert.NotNull(claimed);
        await _worktrees.CreateAsync(issue.Id, "main");
        var worktreePath = _worktrees.WorktreePathFor(issue.Id);
        Assert.True(Directory.Exists(worktreePath));
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = $"agent/{issue.Id}",
            });

        if (target == DispatchCheckpoint.WorktreeAcquired)
        {
            await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.WorktreeAcquired);
            return issue.Id;
        }

        // Stages after worktree: simulate the agent's edit + commit.
        File.WriteAllText(Path.Combine(worktreePath, "edit.txt"), "agent output");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.WorktreeAcquired);

        if (target == DispatchCheckpoint.AgentCompleted)
        {
            await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted);
            return issue.Id;
        }

        if (commitChanges)
        {
            var commit = await _worktrees.CommitAllAsync(worktreePath, $"Task({issue.Id}): x");
            Assert.True(commit.HasChanges);
        }
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted);
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone);
        // (skip PushDone / PrOpened for the agent_completed case)

        if (target == DispatchCheckpoint.CommitDone) return issue.Id;

        // Push for push_done + pr_opened tests.
        await _worktrees.PushAsync(worktreePath, $"agent/{issue.Id}");
        var headSha = await _worktrees.GetHeadShaAsync(worktreePath);
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object> { ["branchSha"] = headSha });
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone);

        if (target == DispatchCheckpoint.PushDone) return issue.Id;

        // Open a PR.
        var pr = await _gitHub.CreatePullRequestAsync(
            $"[{issue.Id}] task",
            $"Task: {issue.Id}",
            $"agent/{issue.Id}", "main");
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object> { ["prNumber"] = pr.Number });
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PrOpened);
        if (withPrNumber)
        {
            // pr_opened + recorded prNumber -> leave alone.
            return issue.Id;
        }
        return issue.Id;
    }

    [Fact]
    public void Classify_PreCheckpoint_LeavesAlone()
    {
        var issue = new IssueRecord("i1", "i1", "task", "x", null,
            IssueStatus.InProgress, 2, "kilo",
            DateTime.UtcNow, DateTime.UtcNow, null, "{}", ParentIssueId: null,
            DispatchCheckpoint: null, CheckpointAt: null, RecoveryAttempts: 0);
        var d = _recovery.Classify(issue);
        Assert.Equal(RecoveryAction.LeftAlone, d.Action);
    }

    [Fact]
    public async Task Classify_PrOpened_WithPrNumber_LeavesAlone()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.PrOpened, withPrNumber: true);
        var issue = (await _issues.GetAsync(id))!;
        var d = _recovery.Classify(issue);
        Assert.Equal(RecoveryAction.LeftAlone, d.Action);
    }

    [Fact]
    public async Task Classify_CommitDone_WithWorktree_Replays()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.CommitDone);
        var issue = (await _issues.GetAsync(id))!;
        var d = _recovery.Classify(issue);
        Assert.Equal(RecoveryAction.Replay, d.Action);
    }

    [Fact]
    public async Task Classify_WorktreeAcquired_MissingWorktree_Fails()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.WorktreeAcquired);
        // Remove the worktree directory.
        var wp = _worktrees.WorktreePathFor(id);
        Directory.Delete(wp, recursive: true);
        var issue = (await _issues.GetAsync(id))!;
        var d = _recovery.Classify(issue);
        Assert.Equal(RecoveryAction.Failed, d.Action);
        Assert.Contains("missing", d.Reason);
    }

    [Fact]
    public async Task Replay_AgentCompleted_CommitsPushesOpensPr()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.AgentCompleted);
        var issue = (await _issues.GetAsync(id))!;
        var action = await _recovery.ReplayAsync(issue);
        Assert.Equal("replay", action.Action);
        Assert.Equal("pr_opened", action.AfterCheckpoint);
        // StubGitHub saw a CreatePullRequestAsync call.
        Assert.Equal(1, _gitHub.CallCount);
        // Issue state advanced to pr_opened with prNumber.
        var after = (await _issues.GetAsync(id))!;
        Assert.Equal(DispatchCheckpoint.PrOpened, after.DispatchCheckpoint);
        Assert.Equal(999, int.Parse(after.GetMetadata("prNumber")!));
        Assert.Equal(1, after.RecoveryAttempts);
    }

    [Fact]
    public async Task Replay_PushDone_OpensPrOnly()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.PushDone);
        var issue = (await _issues.GetAsync(id))!;
        var action = await _recovery.ReplayAsync(issue);
        Assert.Equal("replay", action.Action);
        Assert.Equal(1, _gitHub.CallCount);
    }

    [Fact]
    public async Task Replay_WorktreeAcquired_LeavesAloneForDispatchLoop()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.WorktreeAcquired);
        var issue = (await _issues.GetAsync(id))!;
        var action = await _recovery.ReplayAsync(issue);
        Assert.Equal("left_alone", action.Action);
        Assert.Equal(0, _gitHub.CallCount);
        Assert.Equal(DispatchCheckpoint.WorktreeAcquired, (await _issues.GetAsync(id))!.DispatchCheckpoint);
    }

    [Fact]
    public async Task Replay_FailsAfterMaxAttempts()
    {
        var opts = new StartupRecoveryOptions { MaxAttempts = 2 };
        var recovery = new StartupRecovery(_issues, _reports, _worktrees, _gitHub, _events,
            NullLogger<StartupRecovery>.Instance, opts);
        // Seed without worktree so the recovery fails.
        var id = await SeedIssueAsync(DispatchCheckpoint.WorktreeAcquired);
        var wp = _worktrees.WorktreePathFor(id);
        Directory.Delete(wp, recursive: true);
        var issue = (await _issues.GetAsync(id))!;

        // First attempt: leaves_alone (retry; recoveryAttempts goes 0->1)
        var a1 = await recovery.ReplayAsync(issue);
        Assert.Equal("left_alone", a1.Action);
        var after1 = (await _issues.GetAsync(id))!;
        Assert.Equal(1, after1.RecoveryAttempts);
        Assert.Equal(IssueStatus.InProgress, after1.Status);

        // Bump the recoveryAttempts manually to reach MaxAttempts.
        await _issues.IncrementRecoveryAttemptsAsync(id);

        // Next attempt: hard fail (recoveryAttempts >= MaxAttempts -> transition to Failed).
        var issue2 = (await _issues.GetAsync(id))!;
        var a2 = await recovery.ReplayAsync(issue2);
        Assert.Equal("failed", a2.Action);
        var after2 = (await _issues.GetAsync(id))!;
        Assert.Equal(IssueStatus.Failed, after2.Status);
        Assert.Contains("directory missing", after2.GetMetadata("lastError") ?? "");
    }

    [Fact]
    public async Task RunAsync_Sweep_SixFixtureStates()
    {
        // Build 6 issues in different states:
        //  1. claimed (just claimed, no worktree yet) -> left alone
        //  2. worktree_acquired (worktree exists) -> left alone
        //  3. worktree_acquired (worktree missing) -> failed
        //  4. agent_completed -> replay -> pr_opened
        //  5. push_done -> replay -> pr_opened
        //  6. pr_opened with prNumber -> left alone
        var claimedOnly = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "claimed"));
        await _issues.ClaimAsync(claimedOnly.Id, "kilo");
        await _issues.SetCheckpointAsync(claimedOnly.Id, DispatchCheckpoint.Claimed);

        var wtOk = await SeedIssueAsync(DispatchCheckpoint.WorktreeAcquired);

        var wtMissing = await SeedIssueAsync(DispatchCheckpoint.WorktreeAcquired);
        Directory.Delete(_worktrees.WorktreePathFor(wtMissing), recursive: true);

        var agentDone = await SeedIssueAsync(DispatchCheckpoint.AgentCompleted);

        var pushDone = await SeedIssueAsync(DispatchCheckpoint.PushDone);

        var prOpened = await SeedIssueAsync(DispatchCheckpoint.PrOpened, withPrNumber: true);

        var reportId = await _recovery.RunAsync();
        var report = (await _reports.GetAsync(reportId))!;

        Assert.Equal(6, report.IssuesScanned);
        Assert.Equal(2, report.IssuesReplayed);     // agent_done + push_done
        // wt_missing is left_alone (recoveryAttempts < MaxAttempts -> retry).
        // A separate test (Replay_FailsAfterMaxAttempts) covers the
        // hard-fail transition when recoveryAttempts >= MaxAttempts.
        Assert.Equal(0, report.IssuesFailed);
        Assert.Equal(4, report.IssuesScanned - report.IssuesReplayed - report.IssuesFailed);  // 4 left_alone

        var actions = JsonSerializer.Deserialize<List<RecoveryActionRecord>>(report.ActionsJson)!;
        Assert.Equal(6, actions.Count);

        // Verify each issue's final state.
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(claimedOnly.Id))!.Status);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(wtOk))!.Status);
        // wt_missing is left_alone (recoveryAttempts < MaxAttempts -> retry).
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(wtMissing))!.Status);
        Assert.Equal(1, (await _issues.GetAsync(wtMissing))!.RecoveryAttempts);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(agentDone))!.Status);
        Assert.Equal(DispatchCheckpoint.PrOpened, (await _issues.GetAsync(agentDone))!.DispatchCheckpoint);
        Assert.Equal(DispatchCheckpoint.PrOpened, (await _issues.GetAsync(pushDone))!.DispatchCheckpoint);
        Assert.Equal(DispatchCheckpoint.PrOpened, (await _issues.GetAsync(prOpened))!.DispatchCheckpoint);

        // GitHub saw 3 PR creations: 1 from the seed for the
        // prOpened fixture, plus 1 each from the recoverer for
        // agent_done and push_done.
        Assert.Equal(3, _gitHub.CallCount);
    }

    [Fact]
    public async Task RecoveryAction_IsEmittedToDashboard()
    {
        var id = await SeedIssueAsync(DispatchCheckpoint.PushDone);
        var issue = (await _issues.GetAsync(id))!;
        await _recovery.ReplayAsync(issue);
        var evts = _events.GetHistorySnapshot().Where(e => e.Kind == DashboardEventKind.RecoveryAction).ToList();
        Assert.NotEmpty(evts);
        Assert.Equal(id, evts[0].TaskId);
        Assert.Equal("replay", evts[0].Data?["action"]?.ToString());
    }
}