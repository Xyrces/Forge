# Runtime reference

Use this page when you need a setting, command, or place to look for a problem. For installation, start with the [K3s runbook](../deploy/k3s/README.md). For the application workflow, start with the [user guide](user-guide.md).

## Contents

- [Runtime modes](#runtime-modes)
- [Configuration](#configuration)
- [Models and concurrency](#models-and-concurrency)
- [Command line](#command-line)
- [State and files](#state-and-files)
- [Logs and health](#logs-and-health)
- [Gates, recovery, and secrets](#gates-recovery-and-secrets)

## Runtime modes

The current deployment uses K3s with embedded etcd, Azure SQL, a controller, and worker Jobs. Release Jobs handle controller replacement and rollback independently. Follow the installer and environment runbooks for that deployment. [K3s installation and releases](../deploy/k3s/README.md) · [Project environments and promotion](deployment-environments.md)

Local development and existing systemd installations can still run the in-process orchestrator with SQLite or SQL Server. The CLI modes below apply to that runtime. With `controller.enabled=true`, startup is selected through `controller.mode` (`initialize`, `standby`, or `worker`), and `--check` uses the distributed preflight. Other CLI modes are rejected. [Legacy systemd deployment](linux-deployment.md)

## Configuration

Settings load in this order, with later values taking precedence.

1. Built-in defaults.
2. `appsettings.json` in the current working directory.
3. The file supplied through `--config <path>`.
4. Environment variables, using `__` for nested keys.

`--config` overlays the working directory's `appsettings.json`. Its path is resolved from the current working directory, and the supplied file must exist. File changes take effect when the process restarts.

For example, `db__provider` selects the database provider and `Forgesystem__DataRoot` selects the data directory. `GITHUB_TOKEN` is a supported GitHub fallback. `LLM_API_KEY` updates the provider named by `LLM_PROVIDER_NAME`, or the configured default provider. Its companion variables include `LLM_BASE_URL`, `LLM_MODEL`, `LLM_API`, `LLM_PROJECT_ID`, `LLM_ORG_ID`, and `LLM_REASONING_EFFORT`. These provider overrides are applied when `LLM_API_KEY` is present. [Settings loader](../Configuration/OptionsLoader.cs)

| Setting | What it controls |
|---|---|
| `llm.providers`, `llm.defaultProvider`, `llm.roles` | Provider connections and default model assignments. |
| `llm.maxConcurrentRequests`, `llm.overloadRetryCount` | Provider request concurrency and transient overload retries. |
| `budget` | Paid-call admission, spending ceiling, per-request limits, and configured model reservation prices. |
| `controller`, `worker`, `bootstrap` | Distributed controller identity, worker Jobs, and initialization. Use the K3s installer to supply these. |
| `db.provider`, `db.connectionString` | Application database. K3s uses SQL Server credentials supplied by Kubernetes Secret references. |
| `forgesystem.dataRoot` | Forge-owned files, managed checkouts, and project state directories. |
| `github` | Fallback repository credentials. GitHub App identities and project credentials have their own configuration. |
| `dashboard` | Dashboard binding and port. The in-process default is `127.0.0.1:4097`. |
| `messaging` | Internal event transport. |
| `orchestrator.execution` | In-process or Durable Task Scheduler dispatch for the corresponding runtime. |

Projects belong to the database registry. Add them from **Projects** or `POST /api/projects`. The old `projects[]` configuration array is ignored at boot. Registering a distributed project also provisions its owning schema and worker access. [Distributed project registration](distributed-project-registration.md)

The repository's `appsettings.example.json` contains older provider and in-process examples. Check the settings against your intended runtime before using it. Configuration classes describe the available fields, while the deployment runbook describes how to supply private configuration to K3s. [Application settings](../Configuration/AgentOptions.cs) · [Controller, worker, and budget settings](../Configuration/ControllerOptions.cs)

## Models and concurrency

Use **Agents** to inspect each role's effective model, prompt, running work, and recent run. Role models can be changed there without restarting Forge. Normal model selection uses the project override, then the global override, then `llm.roles`, then the provider default. An override must name a configured provider. [Model resolution](../Agents/RoleModelOverrides.cs)

The Intake, Designer, Artist, and Groomer roles prepare work for a sprint. CoreDev and ClientDev implement changes, QA verifies them, and the Reviewer checks pull requests. The Orchestrator coordinates dispatch. A project can supply its own role prompts under `agents/`. [Role definitions](../Agents/RoleAgentRegistry.cs)

Project role caps control how many tasks of each role may run together. Adjust them on the project's configuration page or through `PUT /api/projects/{id}/roles`. Provider concurrency is a separate limit on simultaneous model requests. `spawner.maxConcurrentSessions` is a legacy setting and does not set engineering role capacity.

Provider cooldowns apply across subsystems within the process. Check the effective provider and model when work is waiting after a rate-limit response. The **API spending** section on **Now** shows the budget position, including reserved requests and settled usage. The same information is available from `GET /api/budget`. The available Forge budget is an authorization balance, and provider billing can require separate reconciliation. [Budget accounting and evidence](workflow-audit/budget.md)

## Command line

Run source commands from the repository root. The executable project is `Forge.Core/Forge.Core.csproj`.

```sh
dotnet run --project Forge.Core/Forge.Core.csproj -- --config /path/to/appsettings.json --check
```

`--check` reports preflight failures and exits nonzero on failure. The distributed runtime performs its own controller checks. In-process preflight checks configuration, stores, GitHub access, and provider authentication.

For a published executable, use `dotnet Forge.Core.dll --config /path/to/appsettings.json --check` from its publish directory.

The following modes are for local development and existing in-process installations with `controller.enabled=false`.

| Argument | Action |
|---|---|
| No mode argument | Start the orchestrator and configured dashboard. |
| `--status` | Print the primary project's queue summary and exit. |
| `--once` | Recognized by the parser, but currently starts the continuous runtime. Avoid using it for one-cycle automation. |
| `--dashboard-only` | Host the dashboard without the engineering dispatch loop. |
| `--recover` | Report startup recovery actions as a dry run. |
| `--recover-and-start` | Replay unfinished effects, print the recovery report, and exit. The current implementation requires a separate normal startup afterward. |
| `--enqueue-task "<title>"` | Add a task using `--task-type`, `--task-desc`, and `--branch`. Use the ID returned by Forge. New initiatives normally start through Intake. |
| `--worktree-smoke` | Create a worktree, commit a marker file, and inspect the result. Leaves the worktree and branch in place and prints cleanup commands. |
| `--migrate-db --target sqlserver` | Run the legacy SQLite-to-SQL Server migration. Stop the service and follow the cutover runbook. |
| `--init-azure-sql` | Provision the contained database user for a managed identity using an Entra administrator connection. |

Migration and identity provisioning accept additional options described in the cutover guide. Check that guide before executing either command, particularly the destructive `--reset` option. [Azure SQL cutover](azure-sql-cutover.md) · [CLI implementation](../Program.cs)

## State and files

The database owns project registration, issues, sprints, memory, reviews, runs, and other durable application records. In K3s, Azure SQL holds that state and a persistent volume at `/var/lib/forge` holds local files. Preserve the DataProtection keyring at `/var/lib/forge/keyring` across releases so encrypted project secrets remain readable.

For in-process installations, `forgesystem.dataRoot` overrides the platform default. Without an override, Forge uses `%LOCALAPPDATA%/Forge` on Windows, `~/Library/Application Support/Forge` on macOS, or `$XDG_DATA_HOME/forge` on Linux, falling back to `~/.local/share/forge`.

| Location | Purpose |
|---|---|
| `<dataRoot>/projects/<projectId>/` | Managed project checkout. Operator-managed repositories may have a separate root. |
| `<dataRoot>/projects/<projectId>/.forge/state/` | Registered project's local state files. |
| `<dataRoot>/projects/<projectId>/.forge/worktrees/` | Task worktrees. |
| `<dataRoot>/projects/<projectId>/.forge/art-output/` | Generated art output. |
| `<projectRoot>/.portHorizon/state/` | Compatibility location for the legacy `default` project. |

With SQLite, the state directory contains `issues.db` and `memory.db`. With SQL Server, their records live in the database. `issues.jsonl` is a regenerated viewing copy of the issue store. Use database backups for recovery. [Path resolution](../Configuration/ForgesystemPaths.cs) · [Project state paths](../Configuration/ProjectStateDirs.cs)

## Logs and health

Start with the task's history, **Runs**, and **Flow** when work appears stuck. Those show the work's current state and the events that led to it.

| Signal | Where to look |
|---|---|
| Running revision | Dashboard revision indicator or `GET /api/meta/buildinfo`. |
| Application health | `GET /api/forgesystem/health`, `/api/health/heartbeat`, and `/api/health/uptime`. |
| Current HTTP routes | `GET /api/meta/endpoints`. |
| Live events | `GET /api/events` for the server-sent event stream. |
| K3s controller or worker output | The relevant Pod's logs. Release progress is also recorded in the `forge-release-<operation>` ConfigMap. |
| Legacy systemd service | `sudo systemctl status forge` and `sudo journalctl -u forge -f`. |
| Agent diagnostics | `<dataRoot>/logs/agent.log` for agents running in the controller process. With the K3s manifest this is `/var/lib/forge/logs/agent.log`. For worker attempts, use the run transcript and worker Pod logs. |
| Recovery history | `GET /api/recovery/reports?limit=50`, then `GET /api/recovery/reports/{id}` for an individual report. |

The K3s dashboard is cluster-private and has no application authentication. On a workstation, use a loopback-bound SSH tunnel to the service address. For a browser on the Linux host, forward the service to the host’s loopback interface. [Dashboard connection commands](../README.md#3-install-and-open-the-dashboard) · [Release operations](../deploy/k3s/README.md)

## Gates, recovery, and secrets

**Stage gates** hold `design`, `groom`, `sprint`, `merge`, or `deploy`. Use **Flow → Edit** at `/flow?mode=edit` to hold or release stages, or use the stage chips on **Now**. The API provides `GET /api/gates` and `POST /api/gates/{stage}/hold` or `/release`. Holding sprint assembly leaves tasks in the active sprint able to run. **Run quality checks** at `/ops/gates` configures checks on agent plans. Human PR-review requirements and project environment approval policies have their own explicit controls. [Review identities and human approval](github-app-identities.md) · [Environment acceptance and promotion](deployment-environments.md)

**Recovery** reconciles durable task and attempt records with observed work. Inspect the recorded failure before retrying. A failed dependency continues to block dependent work until it is resolved through the application. [Worker recovery](workflow-audit/durable-worker-recovery.md) · [In-process startup recovery](p4-restart-safety.md)

**Secrets** are managed per project at `/projects/{id}/secrets` and stored encrypted. Agent tools consume permitted secrets by reference through their process environment. Keep credential values out of prompts and repository content. The K3s deployment gives controllers, initializers, and workers separate credentials, and mounts GitHub App credentials only where required. [K3s credential boundaries](../deploy/k3s/README.md#sql-identity-separation-and-trust-boundary) · [GitHub App setup](github-app-identities.md)

[Back to the documentation index](README.md)
