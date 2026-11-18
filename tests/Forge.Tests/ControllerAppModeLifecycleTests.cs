using System.Diagnostics;
using Forge.AgentTools;
using Forge.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

[CollectionDefinition("Controller App environment", DisableParallelization = true)]
public sealed class ControllerAppEnvironmentCollection;

[Collection("Controller App environment")]
public sealed class ControllerAppModeLifecycleTests
{
    [Fact]
    public async Task IsolatedWorktreeWorkerReceiptControllerCommitAndCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("app-mode-lifecycle");
        await Git(root, "init", "-b", "main");
        await Git(root, "config", "user.name", "Fixture");
        await Git(root, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(root, "source.txt"), "original");
        await Git(root, "add", "-A"); await Git(root, "commit", "-m", "Initial");
        var service = new GitWorktreeService(new WorkspaceOptions { Root = root, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance,
            publisherToken: () => throw new InvalidOperationException("Local qualification must not obtain a token."),
            publisherRepository: "https://github.com/owner/repo");
        var previous = Environment.GetEnvironmentVariable("FORGE_GITHUB_APPS_FILE");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_GITHUB_APPS_FILE", "fixture-controller-config-not-read-by-local-git");
            var path = await service.CreateAsync("ordinary-worker", "main");
            // Model the separate uncredentialed worker phase; the fixture never has real credentials.
            Environment.SetEnvironmentVariable("FORGE_GITHUB_APPS_FILE", null);
            File.WriteAllText(Path.Combine(path, "source.txt"), "verified change");
            string[] commands = ["git diff --check"];
            var receipt = await WorkerVerification.VerifyAsync("ordinary-attempt", path, commands, NullLogger.Instance, default);
            Environment.SetEnvironmentVariable("FORGE_GITHUB_APPS_FILE", "fixture-controller-config-not-read-by-local-git");
            Assert.True(await WorkerVerification.MatchesAsync(receipt, path, commands, default, root));
            Assert.True((await service.CommitAllAsync(path, "Verified ordinary change")).HasChanges);
            Assert.True(await WorkerVerification.MatchesAsync(receipt, path, commands, default, root));
            Assert.Equal(receipt.TreeSha, await service.GetCommitTreeShaAsync(path, await service.GetHeadShaAsync(path)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunVerification.RunAsync(path, commands, NullLogger.Instance, default));
            await service.RemoveAsync("ordinary-worker");
            Assert.False(Directory.Exists(path));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_GITHUB_APPS_FILE", previous); }
    }

    private static async Task Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var result = await ControllerGitRunner.ExecuteAsync(start, default);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }
}
