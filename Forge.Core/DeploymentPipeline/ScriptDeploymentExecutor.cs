using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

// Runs a project-configured script/command inline (this executor never
// touches Forge's own binaries, so there's no reason to detach or
// hand off to a helper process -- the request/response cycle for
// "approve" can just await the script and report the outcome
// immediately). Covers the cases the user flagged when this feature
// was designed: "some [projects] may just be adding a tag to git" --
// point ScriptPath at a one-liner like `git tag vX.Y.Z && git push
// origin vX.Y.Z`, or at something heavier (a Docker build+push, an
// npm publish, etc).
public sealed class ScriptDeploymentExecutor : IDeploymentExecutor
{
    private readonly ILogger<ScriptDeploymentExecutor> _logger;

    public ScriptDeploymentExecutor(ILogger<ScriptDeploymentExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<DeploymentExecutionResult> ExecuteAsync(
        ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default)
    {
        var scriptPath = project.Deployment?.ScriptPath;
        if (string.IsNullOrWhiteSpace(scriptPath))
            return new DeploymentExecutionResult(false, $"Project '{project.Id}' has DeploymentKind.Script but no ScriptPath configured.");

        var resolvedPath = Path.IsPathRooted(scriptPath) ? scriptPath : Path.Combine(project.Root, scriptPath);
        if (!File.Exists(resolvedPath))
            return new DeploymentExecutionResult(false, $"Deployment script not found: {resolvedPath}");

        var psi = new ProcessStartInfo
        {
            FileName = resolvedPath,
            WorkingDirectory = project.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["FORGE_DEPLOY_PROJECT_ID"] = project.Id;
        psi.Environment["FORGE_DEPLOY_COMMIT_SHA"] = candidate.CommitSha;
        psi.Environment["FORGE_DEPLOY_PROJECT_ROOT"] = project.Root;

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {resolvedPath}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var log = await stdoutTask + await stderrTask;
            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Deployment script for {ProjectId} exited {Code}", project.Id, proc.ExitCode);
                return new DeploymentExecutionResult(false, log);
            }
            return new DeploymentExecutionResult(true, log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deployment script for {ProjectId} threw", project.Id);
            return new DeploymentExecutionResult(false, ex.ToString());
        }
    }
}
