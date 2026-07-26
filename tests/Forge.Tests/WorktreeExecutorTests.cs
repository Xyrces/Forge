using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator.Workflow;
using Xunit;

namespace Forge.Tests;

public class WorktreeExecutorTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _worktrees;

    public WorktreeExecutorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ph-wtexec-" + Guid.NewGuid().ToString("N"));
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
        RunGit(dir, "init -q -b main");
        RunGit(dir, "config user.email test@test");
        RunGit(dir, "config user.name Test");
        RunGit(dir, "config commit.gpgsign false");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        RunGit(dir, "add README.md");
        RunGit(dir, "commit -q -m init");
    }

    private void SetupRemoteAndBranch(string taskId)
    {
        var bareDir = Path.Combine(Path.GetTempPath(), "ph-wtexec-remote-" + Guid.NewGuid().ToString("N"));
        RunGit(Path.GetTempPath(), "init -q --bare " + bareDir);
        RunGit(_workDir, "remote add origin " + bareDir);
        RunGit(_workDir, "push -u origin main");
        var branch = "agent/" + taskId;
        RunGit(_workDir, "checkout -b " + branch);
        File.WriteAllText(Path.Combine(_workDir, "pr-content.txt"), "from-pr");
        RunGit(_workDir, "add pr-content.txt");
        // Use single-word commit message to avoid shell quoting issues
        RunGit(_workDir, "commit -q -m PRcontent");
        RunGit(_workDir, "push -u origin " + branch);
        RunGit(_workDir, "checkout main");
    }

    private static string RunGitWithOutput(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("git " + args + " failed (exit=" + p.ExitCode + "): " + err + output);
        return output.Trim();
    }

    private static void RunGit(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
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
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "someone-else");
        var claimedOk = await ClaimExecutor.HandleAsync(
            issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        Assert.Equal(ClaimResult.AlreadyClaimed, claimedOk.Result);
        var secondClaim = await ClaimExecutor.HandleAsync(
            claimedOk.Issue, _issues, NullLogger<ClaimExecutor>.Instance, default);
        Assert.Equal(ClaimResult.AlreadyClaimed, secondClaim.Result);

        var result = await WorktreeExecutor.HandleAsync(
            secondClaim, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.AlreadyClaimed, result.Result);
        Assert.Null(result.WorktreePath);
    }

    [Fact]
    public async Task ReworkRound_DetectsPrNumberAndReworkAttempts_SyncsToRemoteHead()
    {
        var taskId = "task-42";
        SetupRemoteAndBranch(taskId);

        // Verify remote branch has a commit different from main
        var remoteSha = RunGitWithOutput(_workDir, "rev-parse origin/agent/" + taskId);
        var mainSha = RunGitWithOutput(_workDir, "rev-parse main");
        Assert.NotEqual(remoteSha, mainSha);

        // Verify remote branch has pr-content.txt
        var lsTree = RunGitWithOutput(_workDir, "ls-tree --name-only origin/agent/" + taskId);
        Assert.Contains("pr-content.txt", lsTree);

        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "rework test"));
        await _issues.ClaimAsync(issue.Id, "forge");
        var meta = new Dictionary<string, object>
        {
            ["prNumber"] = "99",
            ["reworkAttempts"] = "2",
        };
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null, metadata: meta);
        var refreshed = await _issues.GetAsync(issue.Id);

        var input = new ClaimedIssue(refreshed!, ClaimResult.Ok, null, "agent/" + taskId);

        var result = await WorktreeExecutor.HandleAsync(
            input, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.Ok, result.Result);
        Assert.NotNull(result.WorktreePath);

        // The worktree should now be at the remote PR head
        var wtSha = RunGitWithOutput(result.WorktreePath!, "rev-parse HEAD");
        Assert.Equal(remoteSha, wtSha);

        Assert.True(File.Exists(Path.Combine(result.WorktreePath!, "pr-content.txt")),
            "Worktree should have pr-content.txt from the remote PR head after sync");
    }

    [Fact]
    public async Task ReworkRound_WithZeroAttempts_DoesNotSync()
    {
        var taskId = "task-43";
        SetupRemoteAndBranch(taskId);
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "no-rework"));
        await _issues.ClaimAsync(issue.Id, "forge");
        var meta = new Dictionary<string, object>
        {
            ["prNumber"] = "100",
            ["reworkAttempts"] = "0",
        };
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null, metadata: meta);
        var refreshed = await _issues.GetAsync(issue.Id);
        var input = new ClaimedIssue(refreshed!, ClaimResult.Ok, null, "agent/" + taskId);

        var result = await WorktreeExecutor.HandleAsync(
            input, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.Ok, result.Result);
        Assert.NotNull(result.WorktreePath);

        // No sync: worktree stays on main
        var wtSha = RunGitWithOutput(result.WorktreePath!, "rev-parse HEAD");
        var mainSha = RunGitWithOutput(_workDir, "rev-parse main");
        Assert.Equal(mainSha, wtSha);

        Assert.False(File.Exists(Path.Combine(result.WorktreePath!, "pr-content.txt")),
            "Worktree should NOT have pr-content.txt because reworkAttempts=0 means no sync");
    }

    [Fact]
    public async Task ReworkRound_WithoutPrNumber_DoesNotSync()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "first-time"));
        await _issues.ClaimAsync(issue.Id, "forge");
        var meta = new Dictionary<string, object>
        {
            ["reworkAttempts"] = "1",
        };
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null, metadata: meta);
        var refreshed = await _issues.GetAsync(issue.Id);
        var input = new ClaimedIssue(refreshed!, ClaimResult.Ok, null, "agent/task-44");

        var result = await WorktreeExecutor.HandleAsync(
            input, _issues, _worktrees, "main", NullLogger<WorktreeExecutor>.Instance, default);

        Assert.Equal(WorktreeResult.Ok, result.Result);
        Assert.NotNull(result.WorktreePath);
    }
}
