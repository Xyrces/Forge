using System.Text.Json;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

// Runs once at Forge startup, before the dispatch loop begins. Its
// only job is to close the loop that SelfHostedWindowsServiceDeploymentExecutor
// opens: that executor hands off to a detached Forge.Deployer process
// and returns immediately (this process is about to be killed by the
// service stop Forge.Deployer triggers), so the Deployed/DeployFailed
// verdict can only ever be recorded by a LATER Forge.Core process --
// whichever one starts next, which is normally the very release that
// was just deployed.
public sealed class DeploymentResultReconciler
{
    private readonly ILogger<DeploymentResultReconciler> _logger;

    public DeploymentResultReconciler(ILogger<DeploymentResultReconciler> logger)
    {
        _logger = logger;
    }

    public async Task ReconcileAsync(
        IReadOnlyList<ProjectOptions> projects,
        Func<string, DeploymentStore> storeFor,
        CancellationToken ct = default)
    {
        foreach (var project in projects)
        {
            var d = project.Deployment;
            if (d?.Kind != DeploymentKind.SelfHostedWindowsService || string.IsNullOrWhiteSpace(d.ReleasesRoot))
                continue;

            var resultDir = Path.Combine(Path.GetDirectoryName(d.ReleasesRoot.TrimEnd('\\', '/'))!, "deploy-status");
            if (!Directory.Exists(resultDir)) continue;

            var store = storeFor(project.Id);
            foreach (var file in Directory.GetFiles(resultDir, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var dto = JsonSerializer.Deserialize<DeployResultDto>(await File.ReadAllTextAsync(file, ct));
                    if (dto is null) continue;

                    var candidate = await store.GetAsync(id, ct);
                    if (candidate is null)
                    {
                        _logger.LogWarning("Deploy result file {File} has no matching deployment row; discarding", file);
                        File.Delete(file);
                        continue;
                    }

                    if (dto.Success)
                    {
                        await store.MarkDeployedAsync(id, dto.Log, ct);
                        _logger.LogInformation("Deployment {Id} for project {ProjectId} completed successfully ({ReleaseDir})",
                            id, project.Id, dto.ReleaseDir);
                    }
                    else
                    {
                        await store.MarkDeployFailedAsync(id, dto.Log, ct);
                        _logger.LogError("Deployment {Id} for project {ProjectId} FAILED. See deployment.deploy_log for details.",
                            id, project.Id);
                    }

                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reconcile deploy result file {File}; leaving it for the next startup", file);
                }
            }
        }
    }

    private sealed record DeployResultDto(bool Success, string ReleaseDir, string Log, DateTime CompletedAtUtc);
}
