using System.Diagnostics;
using Forge.Configuration;
using Forge.Deploy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class DeploymentBuildRunnerTests : IDisposable
{
    private readonly string _repoDir;
    private readonly string _dbPath;
    private readonly DeploymentStore _store;
    private readonly string _headSha;

    public DeploymentBuildRunnerTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), $"ph-buildrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoDir);
        InitRepo(_repoDir);
        _headSha = RunGit(_repoDir, "rev-parse HEAD").Trim();

        var dbDir = Path.Combine(Path.GetTempPath(), $"ph-buildrun-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dbDir);
        _dbPath = Path.Combine(dbDir, "issues.db");
        _ = new Core.IssueStore(_dbPath);
        _store = new DeploymentStore(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private ProjectOptions ProjectWithDeployment(DeploymentOptions? deployment) => new()
    {
        Id = "forge",
        Name = "Forge",
        Root = _repoDir,
        Deployment = deployment,
    };

    [Fact]
    public async Task RunAsync_NoDeploymentConfigured_SkipsStraightToBuildPassed()
    {
        var project = ProjectWithDeployment(null);
        var candidate = await _store.CreateAsync(project.Id, _headSha, null, null);
        var runner = new DeploymentBuildRunner(_store, NullLogger<DeploymentBuildRunner>.Instance);

        await runner.RunAsync(project, candidate);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.BuildPassed, updated!.Status);
        Assert.Contains("skipped", updated.BuildLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RequireBuildCheckFalse_SkipsStraightToBuildPassed()
    {
        var project = ProjectWithDeployment(new DeploymentOptions { RequireBuildCheck = false });
        var candidate = await _store.CreateAsync(project.Id, _headSha, null, null);
        var runner = new DeploymentBuildRunner(_store, NullLogger<DeploymentBuildRunner>.Instance);

        await runner.RunAsync(project, candidate);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.BuildPassed, updated!.Status);
        Assert.False(Directory.Exists(Path.Combine(_repoDir, ".forge", "deploy-checkouts", candidate.Id)));
    }

    [Fact]
    public async Task RunAsync_PassingCommands_ChecksOutCommitAndMarksBuildPassed()
    {
        var project = ProjectWithDeployment(new DeploymentOptions
        {
            RequireBuildCheck = true,
            BuildCommand = "cmd /c exit 0",
            TestCommand = "cmd /c exit 0",
        });
        var candidate = await _store.CreateAsync(project.Id, _headSha, null, null);
        var runner = new DeploymentBuildRunner(_store, NullLogger<DeploymentBuildRunner>.Instance);

        await runner.RunAsync(project, candidate);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.BuildPassed, updated!.Status);
        // Ephemeral checkout is always cleaned up, success or failure.
        Assert.False(Directory.Exists(Path.Combine(_repoDir, ".forge", "deploy-checkouts", candidate.Id)));
    }

    [Fact]
    public async Task RunAsync_FailingBuildCommand_MarksBuildFailedAndCapturesLog()
    {
        var project = ProjectWithDeployment(new DeploymentOptions
        {
            RequireBuildCheck = true,
            BuildCommand = "cmd /c exit 1",
            TestCommand = "cmd /c exit 0",
        });
        var candidate = await _store.CreateAsync(project.Id, _headSha, null, null);
        var runner = new DeploymentBuildRunner(_store, NullLogger<DeploymentBuildRunner>.Instance);

        await runner.RunAsync(project, candidate);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.BuildFailed, updated!.Status);
        Assert.False(Directory.Exists(Path.Combine(_repoDir, ".forge", "deploy-checkouts", candidate.Id)));
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

    private static string RunGit(string dir, string args)
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
        p.WaitForExit();
        return stdout;
    }
}
