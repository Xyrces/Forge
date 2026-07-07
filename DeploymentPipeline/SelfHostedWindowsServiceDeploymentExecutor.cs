using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

// The ONLY deployment kind where "the thing being deployed" and "the
// process running this code" are the same binary. Forge cannot
// overwrite or delete its own open .exe/.dll files while running, so
// this executor:
//   1. Checks out the candidate commit into a fresh, ephemeral,
//      detached-HEAD worktree (same pattern as DeploymentBuildRunner).
//   2. Publishes Forge.Core FROM that worktree into a brand new
//      versioned release folder ({ReleasesRoot}/{sha}/) -- never
//      overwriting a directory that could be "current".
//   3. Publishes Forge.Deployer from the same worktree into a STABLE
//      side folder ({ReleasesRoot}/../deployer/) that is never inside
//      the release/current rotation, so the helper it is about to
//      launch isn't itself subject to the same file-lock problem.
//   4. Launches Forge.Deployer.exe DETACHED (fire-and-forget) with
//      the service name, current-link path, new release dir, and a
//      result-file path, then returns immediately with
//      StillInProgress = true. Forge.Deployer does the actual
//      stop -> repoint junction -> start; Forge.Core's own process is
//      expected to be killed by that stop a few seconds later.
// The eventual Deployed/DeployFailed verdict is written to the result
// file and picked up by DeploymentResultReconciler the next time
// Forge.Core starts (whether that's the new release starting cleanly,
// or an operator manually restarting after a failed swap).
public sealed class SelfHostedWindowsServiceDeploymentExecutor : IDeploymentExecutor
{
    private readonly ILogger<SelfHostedWindowsServiceDeploymentExecutor> _logger;

    public SelfHostedWindowsServiceDeploymentExecutor(ILogger<SelfHostedWindowsServiceDeploymentExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<DeploymentExecutionResult> ExecuteAsync(
        ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default)
    {
        var d = project.Deployment;
        if (d is null || string.IsNullOrWhiteSpace(d.ServiceName) || string.IsNullOrWhiteSpace(d.PublishProject)
            || string.IsNullOrWhiteSpace(d.ReleasesRoot) || string.IsNullOrWhiteSpace(d.CurrentLinkPath))
        {
            return new DeploymentExecutionResult(false,
                $"Project '{project.Id}' has DeploymentKind.SelfHostedWindowsService but is missing one of " +
                "ServiceName/PublishProject/ReleasesRoot/CurrentLinkPath.");
        }
        if (!OperatingSystem.IsWindows())
            return new DeploymentExecutionResult(false, "SelfHostedWindowsService deployment is Windows-only.");

        var log = new StringBuilder();
        var checkoutDir = Path.Combine(project.Root, ".forge", "deploy-checkouts", candidate.Id);
        var releaseDir = Path.Combine(d.ReleasesRoot, candidate.CommitSha);
        var deployerDir = Path.Combine(Path.GetDirectoryName(d.ReleasesRoot.TrimEnd('\\', '/'))!, "deployer");
        var resultDir = Path.Combine(Path.GetDirectoryName(d.ReleasesRoot.TrimEnd('\\', '/'))!, "deploy-status");
        var resultPath = Path.Combine(resultDir, $"{candidate.Id}.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkoutDir)!);
            var add = await RunAsync("git", $"worktree add --detach \"{checkoutDir}\" \"{candidate.CommitSha}\"", project.Root, ct);
            log.AppendLine(add.Log);
            if (add.ExitCode != 0)
                return new DeploymentExecutionResult(false, log.ToString());

            var publishCore = await RunAsync("dotnet",
                $"publish \"{d.PublishProject}\" -c Release -o \"{releaseDir}\"", checkoutDir, ct);
            log.AppendLine(publishCore.Log);
            if (publishCore.ExitCode != 0)
                return new DeploymentExecutionResult(false, log.ToString());

            var deployerProjectPath = Path.Combine("tools", "Forge.Deployer", "Forge.Deployer.csproj");
            var publishDeployer = await RunAsync("dotnet",
                $"publish \"{deployerProjectPath}\" -c Release -o \"{deployerDir}\"", checkoutDir, ct);
            log.AppendLine(publishDeployer.Log);
            if (publishDeployer.ExitCode != 0)
                return new DeploymentExecutionResult(false, log.ToString());

            Directory.CreateDirectory(resultDir);
            var deployerExe = Path.Combine(deployerDir, "Forge.Deployer.exe");
            var args = $"--service-name \"{d.ServiceName}\" --current-link \"{d.CurrentLinkPath}\" " +
                       $"--release-dir \"{releaseDir}\" --result-path \"{resultPath}\"";
            log.AppendLine($"Launching detached: {deployerExe} {args}");
            var psi = new ProcessStartInfo
            {
                FileName = deployerExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = deployerDir,
            };
            Process.Start(psi);

            return new DeploymentExecutionResult(true, log.ToString(), StillInProgress: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SelfHostedWindowsService deploy for {ProjectId} @ {Sha} failed", project.Id, candidate.CommitSha);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up deploy checkout {Dir}", checkoutDir);
            }
        }
    }

    private static async Task<(int ExitCode, string Log)> RunAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, $"$ {fileName} {arguments}\n{stdout}{stderr}");
    }
}
