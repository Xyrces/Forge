using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;

namespace Forge.Tools.Deployer;

// One-shot helper launched detached by Forge.Core when an operator
// approves a SelfHostedWindowsService deployment (Deploy/
// SelfHostedWindowsServiceDeploymentExecutor.cs). Forge.Core cannot
// perform this dance itself: the moment the service is stopped, its
// own process (including whatever thread is running this logic) dies
// too. So this tool runs as an independent process, does the
// stop -> repoint -> start swap, writes a result JSON file Forge.Core
// picks up on its NEXT startup (see DeploymentPipeline/DeploymentResultReconciler.cs),
// and exits.
//
// Usage:
//   Forge.Deployer.exe --service-name Forge --current-link C:\ProgramData\Forge\current
//                       --release-dir C:\ProgramData\Forge\releases\<sha> --result-path <file>
//                       [--wait-seconds 30]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var serviceName = RequireArg(args, "--service-name");
        var currentLink = RequireArg(args, "--current-link");
        var releaseDir = RequireArg(args, "--release-dir");
        var resultPath = RequireArg(args, "--result-path");
        var waitSeconds = int.TryParse(GetArg(args, "--wait-seconds"), out var w) ? w : 30;
        var timeout = TimeSpan.FromSeconds(waitSeconds);

        var log = new List<string>();
        var success = false;
        try
        {
            log.Add($"[{DateTime.UtcNow:O}] Forge.Deployer starting: service={serviceName} link={currentLink} release={releaseDir}");

            using var sc = new ServiceController(serviceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                log.Add($"Stopping service '{serviceName}' (current status: {sc.Status})...");
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                log.Add("Service stopped.");
            }
            else
            {
                log.Add("Service already stopped.");
            }

            RepointCurrentLink(currentLink, releaseDir, log);

            log.Add($"Starting service '{serviceName}'...");
            sc.Refresh();
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            log.Add("Service running.");

            success = true;
        }
        catch (Exception ex)
        {
            log.Add($"EXCEPTION: {ex}");
            success = false;
        }

        var result = new DeployResult(success, releaseDir, string.Join('\n', log), DateTime.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
        }
        catch
        {
            // Best-effort: if we can't even write the result file there's
            // nothing more this process can meaningfully do.
        }

        return success ? 0 : 1;
    }

    // Junctions (not symlinks) so this doesn't require
    // SeCreateSymbolicLinkPrivilege -- the service account (typically
    // LocalSystem, or a dedicated service account granted only
    // service-control + filesystem rights under the releases root)
    // only needs ordinary write access to create a junction.
    private static void RepointCurrentLink(string currentLink, string releaseDir, List<string> log)
    {
        if (Directory.Exists(currentLink))
        {
            var attrs = File.GetAttributes(currentLink);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                log.Add($"Removing existing junction at {currentLink}...");
                Directory.Delete(currentLink);
            }
            else
            {
                // First-ever deploy: 'current' was a plain directory
                // (e.g. hand-placed by the operator during initial
                // install). Move it aside instead of deleting so
                // nothing is silently destroyed.
                var backup = currentLink + ".pre-junction-" + DateTime.UtcNow.Ticks;
                log.Add($"'{currentLink}' is a real directory, not a junction; moving it to {backup}");
                Directory.Move(currentLink, backup);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(currentLink))!);
        log.Add($"Creating junction {currentLink} -> {releaseDir}");
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{currentLink}\" \"{releaseDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start mklink");
        proc.WaitForExit();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        log.Add(stdout);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"mklink failed (exit={proc.ExitCode}): {stderr}");
    }

    private static string RequireArg(string[] args, string key) =>
        GetArg(args, key) ?? throw new ArgumentException($"Missing required argument {key}");

    private static string? GetArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i] == key && i + 1 < args.Length) return args[i + 1];
        return null;
    }
}

internal sealed record DeployResult(bool Success, string ReleaseDir, string Log, DateTime CompletedAtUtc);
