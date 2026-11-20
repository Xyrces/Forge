# Getting started

[Documentation](README.md) · [Project overview](../README.md)

You can use an existing Forge instance, install your own, or work on the code. Pick the path that fits what you're trying to do.

- [Use an existing instance](#use-an-existing-instance)
- [Install Forge](#install-forge)
- [Build and test the code](#build-and-test-the-code)

## Use an existing instance

Open the dashboard through the connection provided by your administrator, then select your project.

1. **Vision Board** holds the project's direction and design guidance. Read the mind map, explore its source, and use the conversation to propose refinements.
2. **Intake** is where you introduce work. Start a conversation, refine the working brief, and review the changes before approving a revision.
3. **Sprints** shows the goal, task cards, acceptance checks, and delivery progress. Open a task to see its implementation, reviews, and any explicit hold.
4. **Deployments** records releases and lets you promote a specific release to another configured environment.

For the rest of the dashboard, see the [user guide](user-guide.md). Project registration, credentials, models, and operating controls are covered in the [runtime reference](runtime-reference.md).

## Install Forge

The current installation path uses a Linux host running K3s, Azure SQL for application state, and OpenAI Responses for model calls. The installer takes a verified image digest and private configuration files. It prints a plan before you apply it.

Start with the [K3s installation and release runbook](../deploy/k3s/README.md). It covers the host requirements, SQL permissions, private package and image access, configuration, installation, and rollback. Keep the dashboard private and connect through its documented port-forward and SSH tunnel.

The bootstrap registers Forge itself as the first project. For another project, register its repository through **Projects**. Before approving work, configure the project's roles, credentials, and delivery environments, and review the spending authorization shared by all projects in the dataset. The [project environment guide](deployment-environments.md) explains how sprint delivery and later release promotions fit together.

## Build and test the code

The commands below follow the repository's Linux CI workflow and run from the checkout root. You need the .NET 10 SDK, Git, Python 3, and access to the private Talaria packages in GitHub Packages. Python is used for the installer and release tests.

```bash
git clone https://github.com/Xyrces/Forge.git
cd Forge
dotnet --version
```

The SDK version should start with `10.0`. The solution's project references are contained in this repository. Talaria is restored as NuGet packages.

Before restoring, make these variables available in your shell through your credential manager or private environment configuration. The checked-in [`nuget.config`](../nuget.config) reads them directly.

| Variable | Value |
|---|---|
| `GITHUB_PACKAGES_USER` | Your GitHub username |
| `GITHUB_PACKAGES_PAT` | A token with `read:packages` permission and access to the private Talaria packages |

Then restore and build.

```bash
dotnet restore Forge.sln
dotnet build Forge.sln --no-restore --configuration Release
```

Run the ordinary test suite with the same explicit live-test exclusions used in CI.

```bash
dotnet test Forge.sln --no-build --configuration Release \
  --filter 'FullyQualifiedName!~RealLlmIntegrationTests&FullyQualifiedName!~MafAgentRunnerLiveTests&FullyQualifiedName!~MeshyLiveSmokeTests'
python3 -m unittest discover -s tests/deployment -v
dotnet run --project tools/e2e-harness --no-build --configuration Release -- --repo-root="$(pwd)"
```

The C# suite covers stores, task transitions, planning, engineering dispatch, reviews, and dashboard endpoints. The Python suite checks installation and release behavior. The end-to-end harness uses a temporary Git repository and a local GitHub fake to exercise the engineering workflow. See the [harness guide](e2e-harness.md) for its scope and diagnostics.

The three excluded test classes exercise live LLM or media services and may incur charges when their credentials are configured. Run them deliberately when the work calls for provider verification.

To run the application from source, use `Forge.Core/Forge.Core.csproj` and an explicit development configuration. The [runtime reference](runtime-reference.md) covers configuration and CLI modes. For contributions, start with the [module boundaries and testing conventions](../CONTRIBUTING.md).
