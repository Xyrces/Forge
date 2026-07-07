using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Deploy;

public sealed class DeploymentExecutorFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DeploymentExecutorFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IDeploymentExecutor? Create(ProjectOptions project) => project.Deployment?.Kind switch
    {
        DeploymentKind.Script => new ScriptDeploymentExecutor(_loggerFactory.CreateLogger<ScriptDeploymentExecutor>()),
        DeploymentKind.SelfHostedWindowsService => new SelfHostedWindowsServiceDeploymentExecutor(
            _loggerFactory.CreateLogger<SelfHostedWindowsServiceDeploymentExecutor>()),
        _ => null,
    };
}
