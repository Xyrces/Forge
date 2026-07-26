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
    public async Task SyncWorktreeToRefAsync_RejectsRemoteRefStartingWithDash()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-dash");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-dash", "main");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-dash", "origin/--upload-pack=malicious"));
        Assert.Contains("must not start with '-'", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Also test leading dash on remoteName portion (after split)
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-dash-2", "-origin/main"));
        Assert.Contains("must not start with '-'", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsRemoteRefWithWhitespace()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-ws");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-ws", "main");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-ws", "origin/agent/task x"));
        Assert.Contains("must not contain whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Tab variant (newline in refPath after split)
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-ws-2", "ori/gin\tagent/task"));
        Assert.Contains("must not contain whitespace", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsRefPathContainingOnlyDash()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-dash-only");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-dash-only", "main");

        // After splitting "origin/-", refPath is "-" which starts with '-'
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-dash-only", "origin/-"));
        Assert.Contains("must not start with '-'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsEmptyRefPath()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-empty");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-empty", "main");

        // remoteRef "origin/" splits to remoteName="origin", refPath=""
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-empty", "origin/"));
        Assert.Contains("must not be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        var initialSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-diverged");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-diverged", "main");

        // Create a feature branch from the initial commit, add a commit,
        // push it. This simulates the PR head that a rework round should sync to.
        var rc = RunGit(_workDir, "checkout -b agent/task-X");
        Assert.Equal(0, rc);

        File.WriteAllText(Path.Combine(_workDir, "task-x-feature.txt"), "feature work");
        rc = RunGit(_workDir, "add task-x-feature.txt");
        Assert.Equal(0, rc);

        rc = RunGit(_workDir, "commit -q -m feature");
        Assert.Equal(0, rc);

        rc = RunGit(_workDir, "push origin agent/task-X");
        Assert.Equal(0, rc);

        var featureBranchSha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(initialSha, featureBranchSha);

        // Verify worktree is still at the initial commit (diverged from feature branch)
        var beforeSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(initialSha, beforeSha);

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

        var pushResult = RunGit(_workDir, "push origin main");
        Assert.Equal(0, pushResult);

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
        RunGit(worktreePath, $"checkout --detach {sha}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-detached", "origin/main"));
        Assert.Contains("detached HEAD", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Synchronous git helper. Drains both stdout and stderr before
    /// WaitForExit to prevent pipe-buffer deadlocks.
    /// </summary>
    private static int RunGit(string dir, string args)
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
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
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
    [Fact]
    public async Task SyncWorktreeToRefAsync_ForceUpdatedRef_SyncsAgain()
    {
        var initialSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-force");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-force", "main");

        // Create a feature branch, add a commit, push it — simulates first round
        var rc = RunGit(_workDir, "checkout -b agent/task-force");
        Assert.Equal(0, rc);

        File.WriteAllText(Path.Combine(_workDir, "round1.txt"), "first round");
        rc = RunGit(_workDir, "add round1.txt");
        Assert.Equal(0, rc);
        rc = RunGit(_workDir, "commit -q -m round1");
        Assert.Equal(0, rc);
        rc = RunGit(_workDir, "push origin agent/task-force");
        Assert.Equal(0, rc);

        var round1Sha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(initialSha, round1Sha);

        // First sync — worktree moves to round1
        await _service.SyncWorktreeToRefAsync(worktreePath, "t-force", "origin/agent/task-force");
        var afterFirstSync = await GetHeadShaAsync(worktreePath);
        Assert.Equal(round1Sha, afterFirstSync);

        // Simulate a force-push (rebase): amend the commit on the feature branch
        rc = RunGit(_workDir, "reset --soft HEAD~1");
        Assert.Equal(0, rc);
        File.WriteAllText(Path.Combine(_workDir, "round2.txt"), "second round (force-pushed)");
        rc = RunGit(_workDir, "add round2.txt");
        Assert.Equal(0, rc);
        rc = RunGit(_workDir, "commit -q -m round2-rebased");
        Assert.Equal(0, rc);

        var round2Sha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(round1Sha, round2Sha);

        // Force-push to the same remote branch
        rc = RunGit(_workDir, "push --force origin agent/task-force");
        Assert.Equal(0, rc);

        // Second sync — worktree must move to round2 (force-updated ref)
        await _service.SyncWorktreeToRefAsync(worktreePath, "t-force", "origin/agent/task-force");
        var afterSecondSync = await GetHeadShaAsync(worktreePath);
        Assert.Equal(round2Sha, afterSecondSync);
    }
}
