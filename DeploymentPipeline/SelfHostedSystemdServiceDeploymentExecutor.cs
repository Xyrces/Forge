using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

/// <summary>
/// Self-hosted systemd service deployment. Builds Forge into a fresh
/// versioned release folder, atomically repoints a symlink at it, and
/// calls <c>systemctl restart &lt;unit&gt;</c>. The unit's ExecStart must
/// point at the symlink path (e.g. <c>/opt/forge/current/Forge.Core.dll</c>),
/// so repointing + restarting is sufficient — no detached helper needed
/// (unlike the Windows-SCM case where stopping one's own service
/// mid-restart is awkward).
///
/// <para>
/// The publish step uses <c>dotnet publish</c> against the candidate
/// commit in a detached-HEAD worktree, copies the UI's <c>wwwroot</c>
/// next to the binary, then swaps the symlink and restarts. The
/// service's <c>StateDirectory=</c> (typically <c>/var/lib/forge</c>) is
/// preserved across deploys — only the binary symlink moves.
/// </para>
/// </summary>
public sealed class SelfHostedSystemdServiceDeploymentExecutor : IDeploymentExecutor
{
    private readonly ILogger<SelfHostedSystemdServiceDeploymentExecutor> _logger;
    public SelfHostedSystemdServiceDeploymentExecutor(ILogger<SelfHostedSystemdServiceDeploymentExecutor> logger) => _logger = logger;

    public async Task<DeploymentExecutionResult> ExecuteAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default)
    {
        var d = project.Deployment;
        if (d is null
            || string.IsNullOrWhiteSpace(d.ServiceName)
            || string.IsNullOrWhiteSpace(d.PublishProject)
            || string.IsNullOrWhiteSpace(d.ReleasesRoot)
            || string.IsNullOrWhiteSpace(d.CurrentLinkPath))
        {
            return new DeploymentExecutionResult(
                false,
                $"Project '{project.Id}' has DeploymentKind.SelfHostedSystemdService but is missing one of ServiceName/PublishProject/ReleasesRoot/CurrentLinkPath.");
        }
        if (!OperatingSystem.IsLinux())
        {
            return new DeploymentExecutionResult(
                false,
                "SelfHostedSystemdService deployment is Linux-only.");
        }

        var log = new StringBuilder();
        var checkoutDir = Path.Combine(project.Root, ".forge", "deploy-checkouts", $"publish-{candidate.Id}");
        var releaseDir = Path.Combine(d.ReleasesRoot, candidate.CommitSha);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkoutDir)!);
            Directory.CreateDirectory(d.ReleasesRoot);

            var add = await RunAsync("git", $"worktree add --detach \"{checkoutDir}\" \"{candidate.CommitSha}\"", project.Root, ct);
            log.AppendLine(add.Log);
            if (add.ExitCode != 0) return new DeploymentExecutionResult(false, log.ToString());

            var publish = await RunAsync(
                "dotnet",
                $"publish \"{d.PublishProject}\" -c Release -o \"{releaseDir}\"",
                checkoutDir,
                ct);
            log.AppendLine(publish.Log);
            if (publish.ExitCode != 0) return new DeploymentExecutionResult(false, log.ToString());

            // The UI's static files (app.css, app.js, _framework/blazor.web.js)
            // live in Forge.UI/wwwroot but `dotnet publish Forge.Core.csproj`
            // doesn't copy them. They live alongside the binary, so copy
            // them from the source tree into the release dir. Without this
            // the dashboard renders unstyled and missing interactive support.
            var uiWwwroot = Path.Combine(checkoutDir, "Forge.UI", "wwwroot");
            if (Directory.Exists(uiWwwroot))
            {
                var uiDst = Path.Combine(releaseDir, "wwwroot");
                await RunAsync("cp", $"-r \"{uiWwwroot}/.\" \"{uiDst}/\"", releaseDir, ct);
                log.AppendLine($"Copied UI wwwroot: {uiWwwroot} -> {uiDst}");
            }

            // Atomic symlink repoint: symlinkat(2) under the hood.
            // Create the new symlink with a tmp suffix, then rename(2)
            // into place — POSIX rename is atomic on the same filesystem.
            var tmpLink = d.CurrentLinkPath + ".new";
            File.Delete(tmpLink);
            // Use ln -s + mv rather than File.CreateSymbolicLink because
            // the target doesn't exist yet (relative path into ReleasesRoot).
            await RunAsync("ln", $"-sfn \"{releaseDir}\" \"{tmpLink}\"", Path.GetDirectoryName(d.CurrentLinkPath) ?? "/", ct);
            await RunAsync("mv", $"-Tf \"{tmpLink}\" \"{d.CurrentLinkPath}\"", "/", ct);
            log.AppendLine($"Repointed {d.CurrentLinkPath} -> {releaseDir}");

            // Restart the service. systemd stop + start is idempotent and
            // synchronous under Type=notify, so this returns once the new
            // process is up. Service is briefly unavailable — typically
            // 1-3s — and the dashboard reconnects via SSE.
            var restart = await RunAsync("systemctl", $"restart {d.ServiceName}", "/", ct);
            log.AppendLine(restart.Log);
            if (restart.ExitCode != 0)
            {
                log.AppendLine("WARN: systemctl restart returned non-zero. The service may be down.");
                return new DeploymentExecutionResult(false, log.ToString());
            }

            log.AppendLine($"Deployed {candidate.CommitSha} to {d.ServiceName} via {d.CurrentLinkPath}");
            return new DeploymentExecutionResult(true, log.ToString());
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
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, $"$ {fileName} {arguments}\n{await proc.StandardOutput.ReadToEndAsync(ct)}{await proc.StandardError.ReadToEndAsync(ct)}");
    }
}