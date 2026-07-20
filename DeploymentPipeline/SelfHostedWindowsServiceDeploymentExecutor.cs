using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

/// <summary>
/// One-shot helper. Computes the target release dir, places it
/// adjacent to <see cref="ReleasesRoot"/> as a sibling
/// <c>.pending-{sha}</c> marker, and EXITS WITHOUT TOUCHING THE SCM
/// SERVICE. Forge.Core picks up the marker on its next start and
/// repoints the current junction before booting in the new process.
/// </summary>
public sealed class SelfHostedWindowsServiceDeploymentExecutor : IDeploymentExecutor
{
    private readonly ILogger<SelfHostedWindowsServiceDeploymentExecutor> _logger;
    public SelfHostedWindowsServiceDeploymentExecutor(ILogger<SelfHostedWindowsServiceDeploymentExecutor> logger) => _logger = logger;

    public async Task<DeploymentExecutionResult> ExecuteAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default)
    {
        var d = project.Deployment;
        if (d is null || string.IsNullOrWhiteSpace(d.ServiceName) || string.IsNullOrWhiteSpace(d.PublishProject)
            || string.IsNullOrWhiteSpace(d.ReleasesRoot) || string.IsNullOrWhiteSpace(d.CurrentLinkPath))
            return new DeploymentExecutionResult(false, $"Project '{project.Id}' has DeploymentKind.SelfHostedWindowsService but is missing one of ServiceName/PublishProject/ReleasesRoot/CurrentLinkPath.");
        if (!OperatingSystem.IsWindows())
            return new DeploymentExecutionResult(false, "SelfHostedWindowsService deployment is Windows-only.");
        var log = new StringBuilder();
        // Distinct path from the build runner's deploy-checkouts/<id>:
        // the build runner drops its checkout in the finally block but
        // file handles / git metadata can linger long enough that a
        // fresh `git worktree add` against the same path here gets
        // "already exists" fatal before the publish step starts.
        // Using a separate deploy-checkouts/publish-<id> dir avoids
        // that race entirely.
        var checkoutDir = Path.Combine(project.Root, ".forge", "deploy-checkouts", $"publish-{candidate.Id}");
        var releaseDir = Path.Combine(d.ReleasesRoot, candidate.CommitSha);
        var siblingReleasesRoot = Path.GetDirectoryName(d.ReleasesRoot.TrimEnd('\\','/'))!;
        var pendingMarker = Path.Combine(siblingReleasesRoot, $".pending-{candidate.CommitSha}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkoutDir)!);
            var add = await RunAsync("git", $"worktree add --detach \"{checkoutDir}\" \"{candidate.CommitSha}\"", project.Root, ct);
            log.AppendLine(add.Log);
            if (add.ExitCode != 0) return new DeploymentExecutionResult(false, log.ToString());
            var publishCore = await RunAsync("dotnet",
                $"publish \"{d.PublishProject}\" -c Release -o \"{releaseDir}\"", checkoutDir, ct);
            log.AppendLine(publishCore.Log);
            if (publishCore.ExitCode != 0) return new DeploymentExecutionResult(false, log.ToString());
            await File.WriteAllTextAsync(pendingMarker,
                $"staged for {candidate.Id} by {candidate.RequestedBy} at {DateTime.UtcNow:O}\nreleaseDir={releaseDir}\n", ct);
            log.AppendLine($"Staged swap via marker: {pendingMarker}");
            return new DeploymentExecutionResult(true, log.ToString(), StillInProgress: true);
        }
        catch (Exception ex)
        {
            log.AppendLine($"EXCEPTION: {ex}");
            return new DeploymentExecutionResult(false, log.ToString());
        }
        finally
        {
            try
            {
                await RunAsync("git", $"worktree remove --force \"{checkoutDir}\"", project.Root, ct);
                await RunAsync("git", "worktree prune", project.Root, ct);
            }
            catch { }
        }
    }

    private static async Task<(int ExitCode, string Log)> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments, WorkingDirectory = workingDirectory,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, $"$ {fileName} {arguments}\n{await proc.StandardOutput.ReadToEndAsync(ct)}{await proc.StandardError.ReadToEndAsync(ct)}");
    }
}