using Forge.Configuration;
using Forge.Deploy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class ScriptDeploymentExecutorTests : IDisposable
{
    private readonly string _root;

    public ScriptDeploymentExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ph-script-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteScript(string relativeName, string body)
    {
        var path = Path.Combine(_root, relativeName);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public async Task ExecuteAsync_SucceedingScript_ReturnsSuccessWithEnvVarsVisible()
    {
        WriteScript("deploy.cmd",
            "@echo off\r\necho project=%FORGE_DEPLOY_PROJECT_ID% commit=%FORGE_DEPLOY_COMMIT_SHA%\r\nexit /b 0");

        var project = new ProjectOptions
        {
            Id = "forge",
            Root = _root,
            Deployment = new DeploymentOptions { Kind = DeploymentKind.Script, ScriptPath = "deploy.cmd" },
        };
        var candidate = new DeploymentCandidate(
            "deploy-1", "forge", "sha123", "sha123 fix widget", DeploymentStatus.Pending,
            DateTime.UtcNow, null, null, null, null, null, null);

        var executor = new ScriptDeploymentExecutor(NullLogger<ScriptDeploymentExecutor>.Instance);
        var result = await executor.ExecuteAsync(project, candidate);

        Assert.True(result.Success);
        Assert.False(result.StillInProgress);
        Assert.Contains("project=forge", result.Log);
        Assert.Contains("commit=sha123", result.Log);
    }

    [Fact]
    public async Task ExecuteAsync_FailingScript_ReturnsFailure()
    {
        WriteScript("deploy.cmd", "@echo off\r\necho oops\r\nexit /b 1");

        var project = new ProjectOptions
        {
            Id = "forge",
            Root = _root,
            Deployment = new DeploymentOptions { Kind = DeploymentKind.Script, ScriptPath = "deploy.cmd" },
        };
        var candidate = new DeploymentCandidate(
            "deploy-1", "forge", "sha123", null, DeploymentStatus.Pending,
            DateTime.UtcNow, null, null, null, null, null, null);

        var executor = new ScriptDeploymentExecutor(NullLogger<ScriptDeploymentExecutor>.Instance);
        var result = await executor.ExecuteAsync(project, candidate);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_MissingScriptPath_ReturnsFailureWithoutThrowing()
    {
        var project = new ProjectOptions
        {
            Id = "forge",
            Root = _root,
            Deployment = new DeploymentOptions { Kind = DeploymentKind.Script },
        };
        var candidate = new DeploymentCandidate(
            "deploy-1", "forge", "sha123", null, DeploymentStatus.Pending,
            DateTime.UtcNow, null, null, null, null, null, null);

        var executor = new ScriptDeploymentExecutor(NullLogger<ScriptDeploymentExecutor>.Instance);
        var result = await executor.ExecuteAsync(project, candidate);

        Assert.False(result.Success);
        Assert.Contains("ScriptPath", result.Log);
    }

    [Fact]
    public async Task ExecuteAsync_ScriptFileDoesNotExist_ReturnsFailureWithoutThrowing()
    {
        var project = new ProjectOptions
        {
            Id = "forge",
            Root = _root,
            Deployment = new DeploymentOptions { Kind = DeploymentKind.Script, ScriptPath = "does-not-exist.cmd" },
        };
        var candidate = new DeploymentCandidate(
            "deploy-1", "forge", "sha123", null, DeploymentStatus.Pending,
            DateTime.UtcNow, null, null, null, null, null, null);

        var executor = new ScriptDeploymentExecutor(NullLogger<ScriptDeploymentExecutor>.Instance);
        var result = await executor.ExecuteAsync(project, candidate);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Log, StringComparison.OrdinalIgnoreCase);
    }
}
