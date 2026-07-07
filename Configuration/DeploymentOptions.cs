namespace Forge.Configuration;

// P8: per-project deployment pipeline configuration. A project opts in
// by setting ProjectOptions.Deployment; the Kind selects which
// IDeploymentExecutor runs when an operator approves a candidate (see
// DeploymentPipeline/DeploymentExecutorFactory.cs). Not every project needs (or
// wants) a build+test verification pass before approval -- e.g. a
// "bump a git tag" deployment has nothing to compile -- so
// RequireBuildCheck defaults to true but can be turned off per project.
public sealed record DeploymentOptions
{
    public DeploymentKind Kind { get; set; } = DeploymentKind.None;

    // Whether requesting a candidate kicks off DeploymentBuildRunner
    // (checks out the commit into a scratch worktree and runs
    // BuildCommand/TestCommand) before the operator is allowed to
    // approve it. When false, candidates go straight to "awaiting
    // approval" with no build gate.
    public bool RequireBuildCheck { get; set; } = true;

    // Command line invoked by DeploymentBuildRunner, split on the
    // first space into (FileName, Arguments). Defaults cover the
    // common .NET case; override for other stacks (npm, make, etc).
    public string BuildCommand { get; set; } = "dotnet build -c Release";
    public string TestCommand { get; set; } = "dotnet test -c Release";

    // --- DeploymentKind.Script ---
    // Path to a script (any interpreter registered on PATH/shebang),
    // executed with FORGE_DEPLOY_PROJECT_ID / FORGE_DEPLOY_COMMIT_SHA /
    // FORGE_DEPLOY_PROJECT_ROOT in the environment. Runs in-process
    // (ScriptDeploymentExecutor) since it isn't Forge's own binaries
    // being replaced -- no service bounce needed.
    public string? ScriptPath { get; set; }

    // --- DeploymentKind.SelfHostedWindowsService ---
    // Only meaningful for a project whose deployment IS Forge
    // redeploying itself: Forge cannot overwrite its own open
    // .exe/.dll files, so this path builds to a fresh versioned
    // release folder and hands off to the detached Forge.Deployer
    // helper (tools/Forge.Deployer) to do the stop -> repoint ->
    // start dance after Forge's own process is about to exit.
    public string? ServiceName { get; set; }
    public string? PublishProject { get; set; }
    public string? ReleasesRoot { get; set; }
    public string? CurrentLinkPath { get; set; }
}

public enum DeploymentKind
{
    None,
    Script,
    SelfHostedWindowsService,
}
