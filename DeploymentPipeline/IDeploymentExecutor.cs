using Forge.Configuration;

namespace Forge.Deploy;

// P8: strategy seam for "what actually happens when an operator
// approves a deployment candidate." Each DeploymentKind
// (Configuration/DeploymentOptions.cs) maps to exactly one
// implementation via DeploymentExecutorFactory. An actual interface
// (rather than GitHubService's "virtual methods on a concrete class"
// pattern) is used here because there are genuinely multiple, mutually
// exclusive runtime strategies selected by config -- not a
// test-vs-production seam.
public interface IDeploymentExecutor
{
    Task<DeploymentExecutionResult> ExecuteAsync(
        ProjectOptions project, DeploymentCandidate candidate, CancellationToken ct = default);
}

// Success + Log describe the OUTCOME KNOWN AT RETURN TIME. For
// StillInProgress = true (only ever set by
// SelfHostedWindowsServiceDeploymentExecutor), Success reflects only
// whether the hand-off to Forge.Deployer succeeded -- the real
// deploy/deploy-failed verdict arrives later via a result file that
// Forge.Core reconciles on its next startup (DeploymentPipeline/DeploymentResultReconciler.cs).
public sealed record DeploymentExecutionResult(bool Success, string Log, bool StillInProgress = false);
