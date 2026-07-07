using System.Text.Json;
using Forge.Configuration;
using Forge.Deploy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class DeploymentResultReconcilerTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly DeploymentStore _store;

    public DeploymentResultReconcilerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ph-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "issues.db");
        _ = new Core.IssueStore(_dbPath);
        _store = new DeploymentStore(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProjectOptions ForgeProject(string releasesRoot) => new()
    {
        Id = "forge",
        Root = _root,
        Deployment = new DeploymentOptions
        {
            Kind = DeploymentKind.SelfHostedWindowsService,
            ReleasesRoot = releasesRoot,
        },
    };

    [Fact]
    public async Task ReconcileAsync_SuccessResultFile_MarksDeployedAndDeletesFile()
    {
        var releasesRoot = Path.Combine(_root, "releases");
        var resultDir = Path.Combine(_root, "deploy-status");
        Directory.CreateDirectory(resultDir);

        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.TryApproveAsync(candidate.Id, "op");
        await _store.SetStatusAsync(candidate.Id, DeploymentStatus.Deploying);

        var resultFile = Path.Combine(resultDir, $"{candidate.Id}.json");
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new
        {
            Success = true,
            ReleaseDir = Path.Combine(releasesRoot, "sha1"),
            Log = "deploy ok",
            CompletedAtUtc = DateTime.UtcNow,
        }));

        var reconciler = new DeploymentResultReconciler(NullLogger<DeploymentResultReconciler>.Instance);
        await reconciler.ReconcileAsync(new[] { ForgeProject(releasesRoot) }, _ => _store);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Deployed, updated!.Status);
        Assert.Equal("deploy ok", updated.DeployLog);
        Assert.False(File.Exists(resultFile));
    }

    [Fact]
    public async Task ReconcileAsync_FailureResultFile_MarksDeployFailed()
    {
        var releasesRoot = Path.Combine(_root, "releases");
        var resultDir = Path.Combine(_root, "deploy-status");
        Directory.CreateDirectory(resultDir);

        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        var resultFile = Path.Combine(resultDir, $"{candidate.Id}.json");
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new
        {
            Success = false,
            ReleaseDir = Path.Combine(releasesRoot, "sha1"),
            Log = "mklink failed",
            CompletedAtUtc = DateTime.UtcNow,
        }));

        var reconciler = new DeploymentResultReconciler(NullLogger<DeploymentResultReconciler>.Instance);
        await reconciler.ReconcileAsync(new[] { ForgeProject(releasesRoot) }, _ => _store);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.DeployFailed, updated!.Status);
        Assert.Equal("mklink failed", updated.DeployLog);
    }

    [Fact]
    public async Task ReconcileAsync_OrphanedResultFile_IsDeletedWithoutThrowing()
    {
        var releasesRoot = Path.Combine(_root, "releases");
        var resultDir = Path.Combine(_root, "deploy-status");
        Directory.CreateDirectory(resultDir);

        var resultFile = Path.Combine(resultDir, "deploy-doesnotexist.json");
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new
        {
            Success = true,
            ReleaseDir = "x",
            Log = "orphan",
            CompletedAtUtc = DateTime.UtcNow,
        }));

        var reconciler = new DeploymentResultReconciler(NullLogger<DeploymentResultReconciler>.Instance);
        await reconciler.ReconcileAsync(new[] { ForgeProject(releasesRoot) }, _ => _store);

        Assert.False(File.Exists(resultFile));
    }

    [Fact]
    public async Task ReconcileAsync_ResultFileArrivesDuringPoll_IsPickedUpWithoutWaitingOutTheFullThreshold()
    {
        var releasesRoot = Path.Combine(_root, "releases");
        var resultDir = Path.Combine(_root, "deploy-status");
        Directory.CreateDirectory(resultDir);

        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.TryApproveAsync(candidate.Id, "op");
        await _store.SetStatusAsync(candidate.Id, DeploymentStatus.Deploying);
        // No result file yet -- simulates Forge.Deployer being a beat
        // behind the new process reaching ReconcileAsync.

        var writeLate = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            var resultFile = Path.Combine(resultDir, $"{candidate.Id}.json");
            await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new
            {
                Success = true,
                ReleaseDir = Path.Combine(releasesRoot, "sha1"),
                Log = "deploy ok (arrived late)",
                CompletedAtUtc = DateTime.UtcNow,
            }));
        });

        var reconciler = new DeploymentResultReconciler(NullLogger<DeploymentResultReconciler>.Instance);
        await reconciler.ReconcileAsync(
            new[] { ForgeProject(releasesRoot) }, _ => _store,
            pollInterval: TimeSpan.FromMilliseconds(50), pollAttempts: 5, stuckThreshold: TimeSpan.FromHours(1));
        await writeLate;

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Deployed, updated!.Status);
        Assert.Equal("deploy ok (arrived late)", updated.DeployLog);
    }

    [Fact]
    public async Task ReconcileAsync_StuckDeployingPastThreshold_IsMarkedDeployFailed()
    {
        var releasesRoot = Path.Combine(_root, "releases");

        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.TryApproveAsync(candidate.Id, "op");
        await _store.SetStatusAsync(candidate.Id, DeploymentStatus.Deploying);
        // No result file ever appears -- simulates Forge.Deployer
        // crashing (or the service failing to restart) before it could
        // write one.

        var reconciler = new DeploymentResultReconciler(NullLogger<DeploymentResultReconciler>.Instance);
        await reconciler.ReconcileAsync(
            new[] { ForgeProject(releasesRoot) }, _ => _store,
            pollInterval: TimeSpan.FromMilliseconds(1), pollAttempts: 2, stuckThreshold: TimeSpan.Zero);

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.DeployFailed, updated!.Status);
        Assert.Contains("timed out", updated.DeployLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_ProjectWithoutSelfHostedDeployment_IsSkipped()
    {
        var scriptProject = new ProjectOptions
        {
            Id = "other",
            Root = _root,
            Deployment = new DeploymentOptions { Kind = DeploymentKind.Script },
        };

        var reconciler = new DeploymentResultReconciler(NullLogger<DeploymentResultReconciler>.Instance);
        // Should not throw even though there's no ReleasesRoot/deploy-status dir at all.
        await reconciler.ReconcileAsync(new[] { scriptProject }, _ => _store);
    }
}
