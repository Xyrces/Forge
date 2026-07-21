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
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-wtexec-{Guid.NewGuid():N}");
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
}