using Forge.Configuration;
using Forge.Deploy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class DeploymentExecutorFactoryTests
{
    private readonly DeploymentExecutorFactory _factory = new(NullLoggerFactory.Instance);

    [Fact]
    public void Create_KindNone_ReturnsNull()
    {
        var project = new ProjectOptions { Id = "p", Deployment = new DeploymentOptions { Kind = DeploymentKind.None } };
        Assert.Null(_factory.Create(project));
    }

    [Fact]
    public void Create_NoDeploymentConfigured_ReturnsNull()
    {
        var project = new ProjectOptions { Id = "p", Deployment = null };
        Assert.Null(_factory.Create(project));
    }

    [Fact]
    public void Create_KindScript_ReturnsScriptExecutor()
    {
        var project = new ProjectOptions { Id = "p", Deployment = new DeploymentOptions { Kind = DeploymentKind.Script } };
        Assert.IsType<ScriptDeploymentExecutor>(_factory.Create(project));
    }

    [Fact]
    public void Create_KindSelfHostedWindowsService_ReturnsSelfHostedExecutor()
    {
        var project = new ProjectOptions { Id = "p", Deployment = new DeploymentOptions { Kind = DeploymentKind.SelfHostedWindowsService } };
        Assert.IsType<SelfHostedWindowsServiceDeploymentExecutor>(_factory.Create(project));
    }
}
