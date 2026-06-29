using PortHorizon.Agents.AgentTools;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class GitWorktreeServiceTests : IDisposable
{
    private readonly string _workDir;
    private readonly GitWorktreeService _service;

    public GitWorktreeServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-gw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _service = new GitWorktreeService(
            new Configuration.WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitWorktreeService>.Instance);

        InitRepo(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static void InitRepo(string dir)
    {
        RunGit(dir, "init -q -b main");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config user.name Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# init");
        RunGit(dir, "add -A");
        RunGit(dir, "commit -q -m initial");
    }

    [Fact]
    public async Task Commit_WithNoChanges_ReturnsNoChangesOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-1");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-1", "main");
        // No edits to the worktree
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

    private static void RunGit(string dir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p.WaitForExit();
    }
}