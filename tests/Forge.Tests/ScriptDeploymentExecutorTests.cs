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
        _root = TempRoot.Instance.NewDirectory("script-deploy");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Deployment scripts are OS-native (cmd/batch on Windows, shell on
    // Linux/macOS) -- ScriptDeploymentExecutor just execs whatever
    // ScriptPath points at, so the test has to write the right kind of
    // script for the host OS, and mark it executable on Unix (git/most
    // deploy tooling expects the file to already carry the exec bit;
    // ScriptDeploymentExecutor deliberately doesn't chmod anything).
    private static readonly string DeployScriptName = OperatingSystem.IsWindows() ? "deploy.cmd" : "deploy.sh";

    private string WriteScript(string relativeName, string body)
    {
        var path = Path.Combine(_root, relativeName);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string SucceedingScriptBody(int exitCode) => OperatingSystem.IsWindows()
        ? $"@echo off\r\necho project=%FORGE_DEPLOY_PROJECT_ID% commit=%FORGE_DEPLOY_COMMIT_SHA%\r\nexit /b {exitCode}"
        : $"#!/bin/sh\necho project=$FORGE_DEPLOY_PROJECT_ID commit=$FORGE_DEPLOY_COMMIT_SHA\nexit {exitCode}";

    [Fact]
    public async Task ExecuteAsync_SucceedingScript_ReturnsSuccessWithEnvVarsVisible()
    {
        WriteScript(DeployScriptName, SucceedingScriptBody(0));

        var project = new ProjectOptions
        {
            Id = "forge",
            Root = _root,
            Deployment = new DeploymentOptions { Kind = DeploymentKind.Script, ScriptPath = DeployScriptName },
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
