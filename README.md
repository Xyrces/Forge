# Forge

Forge is a self-hosted tool for developing software with AI agents. You describe a change, work out what counts as done, and approve the brief. Forge plans a sprint, assigns the work, and follows the pull requests through testing, review, and delivery. You can open the sprint board at any point to see where things stand or ask to review a change yourself.

We use Forge on its own repository. One sprint started with a 404 in the browser console. The dashboard was loading a tool that only developers needed. We wanted to keep it available in development and stop requesting it in production. The review caught an unrelated test edit. When the task got stuck, we put it back in the queue. The fix merged on the original pull request. You can follow the [actual review and correction](docs/workflow-audit/pr150-history.md#review-decisions-and-revisions).

**Contents** · [How Forge works](#how-forge-works) · [Where to start](#where-to-start) · [Documentation index](docs/README.md)

## How Forge works

1. **Give the project direction.** Use the Vision Board to describe what you're building and the design choices you want future work to respect. You can refine the mind map in conversation as the project takes shape.
2. **Agree on a change.** Start an idea in Intake. The conversation and working brief sit together so you can ask for a revision, see exactly what changed, and approve the version you want built.
3. **Follow the sprint.** Forge turns the approved work into tasks and runs independent tasks in parallel when workers are available. Each card links to the work behind it. If a change needs your eyes, you can require a human review while the sprint is underway.
4. **Decide where it ships.** Each project can choose its sprint deployment target. Forge itself closes the sprint when the release passes its deployment checks in the user acceptance testing (UAT) environment. From Deployments, you can pick that tested release and promote it to staging or production.

## Where to start

| If you want to… | Start here |
| --- | --- |
| Use a running Forge instance | [Your first initiative](docs/user-guide.md#start-an-initiative) |
| Install Forge on a Linux host | [K3s installation and release runbook](deploy/k3s/README.md) |
| Build Forge or run its tests | [Build and test from source](docs/getting-started.md#build-and-test-the-code) |
| Look up a command, setting, or log | [Runtime reference](docs/runtime-reference.md) |

The current installation runs a .NET controller and worker jobs on K3s. Azure SQL stores project state, OpenAI's Responses API powers the agents, and GitHub hosts the code and its reviews. The [documentation index](docs/README.md) includes project setup, architecture, and the records behind earlier design decisions.
