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

// Success + Log describe the OUTCOME KNOWN AT RETURN TIME. All
// current executors (Script, SelfHostedSystemdService) report
// synchronously: the swap is complete by the time ExecuteAsync
// returns, so StillInProgress is reserved for future executors
// (e.g. a remote cloud deploy that returns a job id).
public sealed record DeploymentExecutionResult(bool Success, string Log, bool StillInProgress = false);
