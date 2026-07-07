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
    // Forge.Deployer calls ServiceController.Start() + WaitForStatus
    // (Running) BEFORE writing its result file (see
    // tools/Forge.Deployer/Program.cs), and the SCM considers the
    // service "Running" as soon as the generic host's StartAsync
    // returns -- which can happen before this very process reaches
    // this method. Poll briefly rather than treating "file not there
    // yet" as "file never coming".
    private static readonly TimeSpan ResultFilePollInterval = TimeSpan.FromSeconds(1);
    private const int ResultFilePollAttempts = 5;

    // A row stuck at Deploying past this age (measured from
    // approved_at) means Forge.Deployer almost certainly died before
    // writing its result file -- crashed, service failed to restart,
    // access denied on the junction, etc. There is no other process
    // that will ever move this row off Deploying, so surface it as a
    // failure instead of leaving an operator staring at a status that
    // will never change.
    private static readonly TimeSpan StuckDeployingThreshold = TimeSpan.FromMinutes(10);

    private readonly ILogger<DeploymentResultReconciler> _logger;

    public DeploymentResultReconciler(ILogger<DeploymentResultReconciler> logger)
    {
        _logger = logger;
    }

    public async Task ReconcileAsync(
        IReadOnlyList<ProjectOptions> projects,
        Func<string, DeploymentStore> storeFor,
        CancellationToken ct = default) =>
        await ReconcileAsync(projects, storeFor, ResultFilePollInterval, ResultFilePollAttempts, StuckDeployingThreshold, ct);

    // Overload with injectable timing, so tests can exercise the
    // poll-then-give-up path (below) without waiting on real wall-clock
    // delays or manufacturing 10-minute-old rows.
    public async Task ReconcileAsync(
        IReadOnlyList<ProjectOptions> projects,
        Func<string, DeploymentStore> storeFor,
        TimeSpan pollInterval,
        int pollAttempts,
        TimeSpan stuckThreshold,
        CancellationToken ct = default)
    {
        foreach (var project in projects)
        {
            var d = project.Deployment;
            if (d?.Kind != DeploymentKind.SelfHostedWindowsService || string.IsNullOrWhiteSpace(d.ReleasesRoot))
                continue;

            var resultDir = Path.Combine(Path.GetDirectoryName(d.ReleasesRoot.TrimEnd('\\', '/'))!, "deploy-status");
            var store = storeFor(project.Id);

            if (Directory.Exists(resultDir))
                await ProcessResultFilesAsync(resultDir, store, project.Id, ct);

            await ReconcileStillDeployingAsync(resultDir, store, project.Id, pollInterval, pollAttempts, stuckThreshold, ct);
        }
    }

    private async Task ProcessResultFilesAsync(string resultDir, DeploymentStore store, string projectId, CancellationToken ct)
    {
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
                        id, projectId, dto.ReleaseDir);
                }
                else
                {
                    await store.MarkDeployFailedAsync(id, dto.Log, ct);
                    _logger.LogError("Deployment {Id} for project {ProjectId} FAILED. See deployment.deploy_log for details.",
                        id, projectId);
                }

                File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile deploy result file {File}; leaving it for the next startup", file);
            }
        }
    }

    // Handles two cases for rows still sitting at Deploying after the
    // pass above: (1) Forge.Deployer is still mid-flight and its result
    // file simply hasn't landed yet -- worth a short poll; (2) it died
    // without ever writing one -- worth giving up on eventually rather
    // than leaving the row stuck forever.
    private async Task ReconcileStillDeployingAsync(
        string resultDir, DeploymentStore store, string projectId,
        TimeSpan pollInterval, int pollAttempts, TimeSpan stuckThreshold, CancellationToken ct)
    {
        var stillDeploying = (await store.ListAsync(projectId, limit: 200, ct: ct))
            .Where(c => c.Status == DeploymentStatus.Deploying)
            .ToList();
        if (stillDeploying.Count == 0) return;

        for (var attempt = 0; attempt < pollAttempts; attempt++)
        {
            await Task.Delay(pollInterval, ct);
            if (Directory.Exists(resultDir))
                await ProcessResultFilesAsync(resultDir, store, projectId, ct);

            stillDeploying = (await store.ListAsync(projectId, limit: 200, ct: ct))
                .Where(c => c.Status == DeploymentStatus.Deploying)
                .ToList();
            if (stillDeploying.Count == 0) return;
        }

        foreach (var candidate in stillDeploying)
        {
            var since = candidate.ApprovedAt ?? candidate.RequestedAt;
            if (DateTime.UtcNow - since < stuckThreshold) continue;

            await store.MarkDeployFailedAsync(candidate.Id,
                "Deploy timed out: no result file appeared from Forge.Deployer after " +
                $"{stuckThreshold.TotalMinutes:0} minute(s). The helper process likely crashed, " +
                "or the service failed to restart -- check the Windows Event Log and " +
                "C:\\ProgramData\\Forge\\deployer for details.", ct);
            _logger.LogError(
                "Deployment {Id} for project {ProjectId} timed out waiting for a result file and was marked DeployFailed",
                candidate.Id, projectId);
        }
    }

    private sealed record DeployResultDto(bool Success, string ReleaseDir, string Log, DateTime CompletedAtUtc);
}
