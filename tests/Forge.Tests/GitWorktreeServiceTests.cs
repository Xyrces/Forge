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
        // Initialize main repo
        RunGit(dir, "init -q -b main");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config user.name Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# init");
        RunGit(dir, "add -A");
        RunGit(dir, "commit -q -m initial");

        // Create a bare clone so we have a valid 'origin' remote
        // that the sync method can fetch from
        RunGit(dir, $"clone --bare {dir} {_bareDir}");
        RunGit(dir, $"remote add origin {_bareDir}");
        RunGit(dir, "fetch origin");
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
    public async Task SyncWorktreeToRef_DivergedBranch_SyncsToRemoteRef()
    {
        var worktreePath = _service.WorktreePathFor("t-diverged");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-diverged", "main");

        var beforeSha = await GetHeadShaAsync(worktreePath);

        // Advance the default branch (simulates external changes merged into main)
        File.WriteAllText(Path.Combine(_workDir, "new-file.txt"), "external change");
        RunGit(_workDir, "add -A");
        RunGit(_workDir, "commit -q -m external");
        // Push the new commit to the bare repo so sync can fetch it
        RunGit(_workDir, "push origin main");

        var defaultHeadSha = await GetHeadShaAsync(_workDir);

        Assert.NotEqual(beforeSha, defaultHeadSha);

        await _service.SyncWorktreeToRefAsync(worktreePath, "t-diverged", "origin/main");

        var afterSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(defaultHeadSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRef_FreshTaskWorktree_NoOpWhenAlreadyAtRef()
    {
        var worktreePath = _service.WorktreePathFor("t-fresh");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-fresh", "main");

        var beforeSha = await GetHeadShaAsync(worktreePath);

        await _service.SyncWorktreeToRefAsync(worktreePath, "t-fresh", "origin/main");

        var afterSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRef_DetachedHead_Throws()
    {
        var worktreePath = _service.WorktreePathFor("t-detached");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-detached", "main");

        var sha = await GetHeadShaAsync(worktreePath);
        RunGit(worktreePath, $"checkout --detach {sha}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-detached", "origin/main"));
        Assert.Contains("detached HEAD", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        await p.WaitForExitAsync();
        return stdout.Trim();
    }
}
