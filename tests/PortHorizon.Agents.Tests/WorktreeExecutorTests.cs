using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Orchestrator.Workflow;
using Xunit;

namespace PortHorizon.Agents.Tests;

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
            claimed, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.Ok, result.Result);
        Assert.NotNull(result.WorktreePath);
        Assert.True(Directory.Exists(result.WorktreePath!));
        Assert.Equal("main", result.BaseBranch);
    }

    [Fact]
    public async Task Create_AlreadyClaimed_ReturnsAlreadyClaimed()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimedOk = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        // Second claim attempt:
        var claimedDup = await ClaimExecutor.HandleAsync(
            claimedOk.Issue, _issues, NullLogger<ClaimExecutor>.Instance, default);

        var result = await WorktreeExecutor.HandleAsync(
            claimedDup, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.AlreadyClaimed, result.Result);
        Assert.Null(result.WorktreePath);
    }
}