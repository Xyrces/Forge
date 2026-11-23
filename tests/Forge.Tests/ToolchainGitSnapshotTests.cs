using System.Diagnostics;
using Forge.AgentTools;
using Xunit;

namespace Forge.Tests;

public sealed class ToolchainGitSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotSupportsStandaloneAndLinkedWorktreesWithoutSourceConfig(bool linked)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "toolchain-git-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo"); var snapshot = Path.Combine(root, "snapshot");
        Directory.CreateDirectory(repo); Directory.CreateDirectory(snapshot);
        try
        {
            Git(repo, "init", "-q"); Git(repo, "config", "user.name", "Test"); Git(repo, "config", "user.email", "test@example.invalid");
            File.WriteAllText(Path.Combine(repo, "source.txt"), "base"); Git(repo, "add", "."); Git(repo, "commit", "-qm", "base");
            Git(repo, "config", "credential.helper", "do-not-export-test-helper");
            Git(repo, "remote", "add", "origin", "https://example.invalid/repo");
            var worktree = repo;
            if (linked)
            {
                worktree = Path.Combine(repo, ".forge", "worktrees", "task");
                Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
                Git(repo, "worktree", "add", "-b", "task", worktree);
            }
            File.WriteAllText(Path.Combine(worktree, "source.txt"), "prior task edits");
            File.WriteAllText(Path.Combine(worktree, "new.txt"), "untracked task work");
            await ToolchainGitSnapshot.PrepareAsync(worktree, snapshot, default);
            var privateGit = Path.Combine(snapshot, "repo");
            Assert.Equal(Git(worktree, "rev-parse", "HEAD"), Git(root, "--git-dir", privateGit, "rev-parse", "HEAD"));
            Assert.Equal("base", Git(root, "--git-dir", privateGit, "show", "HEAD:source.txt").Trim());
            var config = File.ReadAllText(Path.Combine(privateGit, "config"));
            Assert.DoesNotContain("credential", config); Assert.DoesNotContain("origin", config);
            var status = Git(root, "--git-dir", privateGit, "--work-tree", worktree, "status", "--porcelain");
            Assert.Contains("source.txt", status); Assert.Contains("new.txt", status);
            Assert.Equal("prior task edits", File.ReadAllText(Path.Combine(worktree, "source.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, error); return output;
    }
}
