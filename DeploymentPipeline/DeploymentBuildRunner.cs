using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

// P8: build/test verification gate that runs before a deployment
// candidate is eligible for approval. Checks out the candidate's
// commit into an ephemeral, detached-HEAD worktree (NOT the
// branch-based agent worktrees GitWorktreeService manages -- this is
// a throwaway checkout that's always removed at the end of the run,
// success or failure) and shells out to the project's configured
// BuildCommand/TestCommand.
//
// Skips straight to BuildPassed when the project either has no
// DeploymentOptions or has RequireBuildCheck = false -- e.g. a
// git-tag-only deployment has nothing to compile.
public sealed class DeploymentBuildRunner
{
    // Build/test output is stored as a single TEXT column read back
    // wholesale into the UI's <pre>; cap it well below "a chatty test
    // runner filled the disk" territory so neither the sqlite row nor
    // the dashboard response balloons unbounded.
    private const int MaxLogChars = 200_000;

    private readonly DeploymentStore _store;
    private readonly ILogger<DeploymentBuildRunner> _logger;

    public DeploymentBuildRunner(DeploymentStore store, ILogger<DeploymentBuildRunner> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RunAsync(ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default)
    {
        var deployment = project.Deployment;
        if (deployment is null || !deployment.RequireBuildCheck)
        {
            await _store.AppendBuildLogAsync(candidate.Id, DeploymentStatus.BuildPassed,
                "Build check skipped (RequireBuildCheck=false or no deployment configured for this project).", ct);
            return;
        }

        await _store.SetStatusAsync(candidate.Id, DeploymentStatus.BuildRunning, ct);

        var checkoutDir = Path.Combine(project.Root, ".forge", "deploy-checkouts", candidate.Id);
        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkoutDir)!);
            var addResult = await RunAsync("git",
                $"worktree add --detach \"{checkoutDir}\" \"{candidate.CommitSha}\"",
                project.Root, ct);
            log.AppendLine($"$ git worktree add --detach {checkoutDir} {candidate.CommitSha}");
            log.AppendLine(addResult.Output);
            if (addResult.ExitCode != 0)
            {
                await _store.AppendBuildLogAsync(candidate.Id, DeploymentStatus.BuildFailed, Truncate(log), ct);
                return;
            }

            foreach (var (label, command) in new[] { ("build", deployment.BuildCommand), ("test", deployment.TestCommand) })
            {
                if (string.IsNullOrWhiteSpace(command)) continue;
                var (fileName, arguments) = SplitCommand(command);
                log.AppendLine($"$ {command}");
                var result = await RunAsync(fileName, arguments, checkoutDir, ct);
                log.AppendLine(result.Output);
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning("Deployment {Id} {Label} failed (exit={Code})", candidate.Id, label, result.ExitCode);
                    await _store.AppendBuildLogAsync(candidate.Id, DeploymentStatus.BuildFailed, Truncate(log), ct);
                    return;
                }
            }

            await _store.AppendBuildLogAsync(candidate.Id, DeploymentStatus.BuildPassed, Truncate(log), ct);
        }
        catch (Exception ex)
        {
            log.AppendLine($"EXCEPTION: {ex}");
            await _store.AppendBuildLogAsync(candidate.Id, DeploymentStatus.BuildFailed, Truncate(log), ct);
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

    private static string Truncate(StringBuilder log)
    {
        if (log.Length <= MaxLogChars) return log.ToString();
        var full = log.ToString();
        var tail = full[^MaxLogChars..];
        return $"[... truncated {full.Length - MaxLogChars} earlier characters ...]\n{tail}";
    }

    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        var idx = command.IndexOf(' ');
        return idx < 0 ? (command, string.Empty) : (command[..idx], command[(idx + 1)..]);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
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
        return (proc.ExitCode, stdout + stderr);
    }
}
