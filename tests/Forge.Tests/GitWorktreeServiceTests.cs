using System.Diagnostics;
using Forge.AgentTools;
using Forge.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class GitWorktreeServiceTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _bareDir;
    private readonly GitWorktreeService _service;
    private readonly string _wtRoot;

    public GitWorktreeServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ph-gw-" + Guid.NewGuid().ToString("N"));
        _bareDir = _workDir + "-bare.git";
        _wtRoot = Path.Combine(_workDir, ".wt");
        Directory.CreateDirectory(_workDir);
        _service = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);

        InitRepo(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        try { Directory.Delete(_bareDir, recursive: true); } catch { }
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

    private void InitRepo(string dir)
    {
        RunGit(dir, "init -q -b main");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config user.name Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# init");
        RunGit(dir, "add -A");
        RunGit(dir, "commit -q -m initial");

        RunGit(dir, $"clone --bare {dir} {_bareDir}");
        RunGit(dir, $"remote add origin {_bareDir}");
        RunGit(dir, "fetch origin");
    }

    [Fact]
    public async Task Commit_WithNoChanges_ReturnsNoChangesOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-1");
        Directory.CreateDirectory(_wtRoot);
        await _service.CreateAsync("t-1", "main");
        var result = await _service.CommitAllAsync(worktreePath, "msg");
        Assert.Equal(CommitOutcome.NoChanges, result.Outcome);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task Commit_WithNewFile_ReturnsCreatedOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-2");
        Directory.CreateDirectory(_wtRoot);
        await _service.CreateAsync("t-2", "main");
        File.WriteAllText(Path.Combine(worktreePath, "x.txt"), "hello");
        var result = await _service.CommitAllAsync(worktreePath, "msg");
        Assert.Equal(CommitOutcome.Created, result.Outcome);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task SyncWorktreeToRef_DivergedBranch_SyncsToRemoteRef()
    {
        var taskId = "t-diverged";
        var remoteBranch = $"agent/{taskId}";
        var remoteRef = $"origin/{remoteBranch}";

        var mainBeforeSha = RunGitCapture(_workDir, "rev-parse HEAD");

        // Create a remote agent branch with extra commits
        // (simulating a PR head pushed forward between rounds)
        RunGit(_workDir, "checkout -b setupsyncbranch");
        File.WriteAllText(Path.Combine(_workDir, "pr-change.txt"), "rework-content");
        RunGit(_workDir, "add -A");
        var msgPath = Path.Combine(Path.GetTempPath(), "cm-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(msgPath, "external agent work on PR head");
        RunGit(_workDir, $"commit -q -F \{msgPath}\");
        File.Delete(msgPath);
        RunGit(_workDir, $"push origin setupsyncbranch:{remoteBranch}");
        RunGit(_workDir, "fetch origin");
        RunGit(_workDir, "checkout main");
        RunGit(_workDir, "branch -D setupsyncbranch");

        var mainSha = RunGitCapture(_workDir, "rev-parse HEAD");
        var remoteSha = RunGitCapture(_workDir, $"rev-parse origin/{remoteBranch}");

        Assert.Equal(mainBeforeSha, mainSha);
        Assert.NotEqual(mainSha, remoteSha);

        var worktreePath = _service.WorktreePathFor(taskId);
        Directory.CreateDirectory(_wtRoot);
        await _service.CreateAsync(taskId, "main");

        var beforeSha = RunGitCapture(worktreePath, "rev-parse HEAD");
        Assert.Equal(mainSha, beforeSha);
        Assert.NotEqual(beforeSha, remoteSha);

        await _service.SyncWorktreeToRefAsync(worktreePath, taskId, remoteRef);

        var afterSha = RunGitCapture(worktreePath, "rev-parse HEAD");
        Assert.Equal(remoteSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRef_FreshTaskWorktree_NoOpWhenAlreadyAtRef()
    {
        var taskId = "t-fresh";
        var remoteBranch = $"agent/{taskId}";
        var remoteRef = $"origin/{remoteBranch}";

        RunGit(_workDir, $"push origin main:{remoteBranch}");
        RunGit(_workDir, "fetch origin");

        var worktreePath = _service.WorktreePathFor(taskId);
        Directory.CreateDirectory(_wtRoot);
        await _service.CreateAsync(taskId, "main");

        var beforeSha = RunGitCapture(worktreePath, "rev-parse HEAD");
        var remoteSha = RunGitCapture(_workDir, $"rev-parse origin/{remoteBranch}");
        Assert.Equal(beforeSha, remoteSha);

        await _service.SyncWorktreeToRefAsync(worktreePath, taskId, remoteRef);

        var afterSha = RunGitCapture(worktreePath, "rev-parse HEAD");
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRef_DetachedHead_Throws()
    {
        var taskId = "t-detached";
        var worktreePath = _service.WorktreePathFor(taskId);
        Directory.CreateDirectory(_wtRoot);
        await _service.CreateAsync(taskId, "main");

        var sha = RunGitCapture(worktreePath, "rev-parse HEAD");
        RunGit(worktreePath, $"checkout --detach {sha}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, taskId, "origin/main"));
        Assert.Contains("detached HEAD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_FreshTask_WorktreeBranchAtDefaultTip()
    {
        var defaultHeadSha = RunGitCapture(_workDir, "rev-parse HEAD");

        var worktreePath = _service.WorktreePathFor("t-fresh-noop");
        Directory.CreateDirectory(_wtRoot);
        await _service.CreateAsync("t-fresh-noop", "main");

        var worktreeSha = RunGitCapture(worktreePath, "rev-parse HEAD");
        Assert.Equal(defaultHeadSha, worktreeSha);
    }

    private static string RunGitCapture(string dir, string args)
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
        return p.StandardOutput.ReadToEnd().Trim();
    }
}
