using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator.Workflow;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P3 checkpoint 3: WorktreeExecutor creates a git worktree on a
/// per-issue branch. Tests use a real (temp) git repo since the
/// service shells out to <c>git worktree add</c>.
/// </summary>
public class WorktreeExecutorTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _worktrees;

    public WorktreeExecutorTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("wtexec");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, ".portHorizon", "state", "issues.db"));
        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
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

    [Fact]
    public async Task Create_NewIssue_ReturnsOkWithWorktreePath()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);

        var result = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.Ok, result.Result);
        Assert.NotNull(result.WorktreePath);
        Assert.True(Directory.Exists(result.WorktreePath!));
        Assert.Equal("main", result.BaseBranch);
    }

    [Fact]
    public async Task Create_AlreadyClaimed_ReturnsAlreadyClaimed()
    {
        // After P3 wired the orchestrator pre-claims, the workflow
        // no longer double-claims. ClaimExecutor's pre-claim-aware
        // path treats an already-InProgress issue with assignee=forge
        // as Ok (pass-through). To test the AlreadyClaimed sentinel,
        // we use a different assignee on the first claim so the
        // second ClaimExecutor call falls into the standalone path
        // and returns AlreadyClaimed.
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "someone-else");
        var claimedOk = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        // The issue is InProgress with assignee=someone-else, so the
        // pre-claim path is skipped and the standalone claim attempt
        // fails (Status=InProgress). Returns AlreadyClaimed.
        Assert.Equal(ClaimResult.AlreadyClaimed, claimedOk.Result);
        var secondClaim = await ClaimExecutor.HandleAsync(
            claimedOk.Issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        Assert.Equal(ClaimResult.AlreadyClaimed, secondClaim.Result);

        var result = await WorktreeExecutor.HandleAsync(
            secondClaim, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.AlreadyClaimed, result.Result);
        Assert.Null(result.WorktreePath);
    }

    // ---- Stale-reuse sync (observed live 2026-07-26: tasks
    // 185/186/189 died at the plan gate because their reused
    // worktrees predated files that landed on main) ----

    private string InitRepoWithOrigin()
    {
        // Re-root the fixture repo with a bare origin so fetches and
        // origin/main refs work.
        var bare = _workDir + "-bare.git";
        Run("git", $"clone --bare {_workDir} {bare}", _workDir);
        Run("git", "remote add origin " + bare, _workDir);
        Run("git", "fetch origin", _workDir);
        Run("git", "branch --set-upstream-to=origin/main main", _workDir);
        return bare;
    }

    private async Task<IssueRecord> DispatchOnceAsync(string title = "x")
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: title));
        var claimed = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        var result = await WorktreeExecutor.HandleAsync(
            claimed, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);
        Assert.Equal(WorktreeResult.Ok, result.Result);
        return (await _issues.GetAsync(issue.Id))!;
    }

    private void AdvanceOriginMain(string bare, string fileName)
    {
        // Commit a new file directly into the bare repo's main via a
        // throwaway clone (bare repos have no worktree).
        var tmp = Path.Combine(_workDir, "adv");
        Run("git", $"clone -q {bare} {tmp}", _workDir);
        Run("git", "config user.email test@test", tmp);
        Run("git", "config user.name Test", tmp);
        File.WriteAllText(Path.Combine(tmp, fileName), "new on main");
        Run("git", "add " + fileName, tmp);
        Run("git", "commit -q -m advance", tmp);
        Run("git", "push -q origin main", tmp);
        Directory.Delete(tmp, recursive: true);
    }

    [Fact]
    public async Task ReusedWorktree_NoLocalCommits_SyncedToOriginMain()
    {
        InitRepoWithOrigin();
        var issue = await DispatchOnceAsync();
        var wt = _worktrees.WorktreePathFor(issue.Id);

        AdvanceOriginMain(_workDir + "-bare.git", "NewFile.cs");

        // Re-dispatch the same issue (still no prNumber): the stale
        // worktree must be synced to the current base.
        var claimed2 = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        await WorktreeExecutor.HandleAsync(
            claimed2, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.True(File.Exists(Path.Combine(wt, "NewFile.cs")),
            "stale reused worktree was not synced to origin/main");
    }

    [Fact]
    public async Task ReusedWorktree_WithLocalCommits_LeftAlone()
    {
        InitRepoWithOrigin();
        var issue = await DispatchOnceAsync();
        var wt = _worktrees.WorktreePathFor(issue.Id);

        // Simulate a died run's partial work: a local-only commit.
        File.WriteAllText(Path.Combine(wt, "partial.cs"), "local work");
        Run("git", "add partial.cs", wt);
        Run("git", "commit -q -m partial", wt);

        AdvanceOriginMain(_workDir + "-bare.git", "NewFile.cs");

        var claimed2 = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        await WorktreeExecutor.HandleAsync(
            claimed2, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.True(File.Exists(Path.Combine(wt, "partial.cs")),
            "local-only work from the died run was wiped");
        Assert.False(File.Exists(Path.Combine(wt, "NewFile.cs")),
            "worktree with local commits must not be reset to base");
    }
}
