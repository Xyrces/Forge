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

    public GitWorktreeServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-gw-{Guid.NewGuid():N}");
        _bareDir = _workDir + "-bare.git";
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

    [Fact]
    public async Task Commit_WithNoChanges_ReturnsNoChangesOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-1");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-1", "main");
        var result = await _service.CommitAllAsync(worktreePath, "msg");
        Assert.Equal(CommitOutcome.NoChanges, result.Outcome);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task Commit_WithNewFile_ReturnsCreatedOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-2");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-2", "main");
        File.WriteAllText(Path.Combine(worktreePath, "x.txt"), "hello");
        var result = await _service.CommitAllAsync(worktreePath, "msg");
        Assert.Equal(CommitOutcome.Created, result.Outcome);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task CreateAsync_FreshTask_WorktreeBranchAtDefaultTip()
    {
        var defaultHeadSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-fresh-noop");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-fresh-noop", "main");

        var worktreeSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(defaultHeadSha, worktreeSha);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_DivergedBranch_SyncsToRef()
    {
        // Record the initial commit SHA (the worktree will start here)
        var initialSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-diverged");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-diverged", "main");

        // Create a feature branch with a new commit that diverges from the initial commit
        // Use RunGitForOutputAsync to properly drain stdout/stderr before WaitForExit
        RunGitForOutputAsync(_workDir, "checkout -b agent/task-X").GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(_workDir, "task-x-feature.txt"), "feature work");
        RunGitForOutputAsync(_workDir, "add -A").GetAwaiter().GetResult();
        RunGitForOutputAsync(_workDir, "commit -q -m 'feature on agent/task-X'").GetAwaiter().GetResult();

        // The feature branch now has a different SHA from initial
        var featureBranchSha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(initialSha, featureBranchSha);

        // Push to bare repo so SyncWorktreeToRefAsync can fetch it
        await RunGitForOutputAsync(_workDir, "push origin agent/task-X");

        // Verify worktree is still at the initial commit (diverged from feature branch)
        var beforeSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(initialSha, beforeSha);
        Assert.NotEqual(featureBranchSha, beforeSha);

        // Sync the worktree to the remote feature branch ref
        await _service.SyncWorktreeToRefAsync(worktreePath, "t-diverged", "origin/agent/task-X");

        var afterSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(featureBranchSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_FreshTask_NoOpWhenAlreadyAtRef()
    {
        var worktreePath = _service.WorktreePathFor("t-fresh-sync");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-fresh-sync", "main");

        // Push main to bare repo so origin/main is available for fetch
        await RunGitForOutputAsync(_workDir, "push origin main");

        var beforeSha = await GetHeadShaAsync(worktreePath);

        await _service.SyncWorktreeToRefAsync(worktreePath, "t-fresh-sync", "origin/main");

        var afterSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_DetachedHead_Throws()
    {
        var worktreePath = _service.WorktreePathFor("t-detached");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-detached", "main");

        var sha = await GetHeadShaAsync(worktreePath);
        await RunGitForOutputAsync(worktreePath, $"checkout --detach {sha}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-detached", "origin/main"));
        Assert.Contains("detached HEAD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private void InitRepo(string dir)
    {
        RunGitSync(dir, "init -q -b main");
        RunGitSync(dir, "config user.email test@example.com");
        RunGitSync(dir, "config user.name Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# init");
        RunGitSync(dir, "add -A");
        RunGitSync(dir, "commit -q -m initial");

        // Create a bare clone so we have a valid 'origin' remote
        // that the sync method can fetch from
        RunGitSync(dir, $"clone --bare {dir} {_bareDir}");
        RunGitSync(dir, $"remote add origin {_bareDir}");
        RunGitSync(dir, "fetch origin");
    }

    /// <summary>
    /// Synchronous git helper that fully drains stdout/stderr before
    /// WaitForExit to avoid deadlocks when output buffers fill.
    /// </summary>
    private static void RunGitSync(string dir, string args)
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
        // Drain both streams to prevent deadlock
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    /// <summary>
    /// Async git helper that drains both streams to avoid deadlocks.
    /// </summary>
    private static async Task RunGitForOutputAsync(string dir, string args)
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
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await stdoutTask;
        await stderrTask;
    }

    private async Task<string> GetHeadShaAsync(string repoPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Trim();
    }
}
