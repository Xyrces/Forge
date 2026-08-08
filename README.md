# Forge

A long-lived .NET 10 orchestrator that drives AI coding agents against any registered git project. Forge owns the task queue, sprint assembly, git worktrees, the GitHub PR lifecycle, and the review-gated merge — **the model owns the code.**

Forge is project-agnostic. Projects live in a DB-backed registry (the `project` table) and are added through the dashboard or the API — there is no hardcoded target repo in the codebase. Forge's first deployment target was the [Xyrces/PortHorizon](https://github.com/Xyrces/PortHorizon) Godot-ECS game repo, and Forge now also builds itself: the `forge` project is registered like any other and its tasks dispatch against this repo.

The runtime uses the [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/overview/agent-framework-overview) 1.12.0 with [`Microsoft.Agents.AI.Workflows`](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows) for the dispatch pipeline. LLM inference goes through configurable OpenAI-compatible or Anthropic-protocol providers (kilo gateway, MiniMax direct, Kimi, …) with per-role provider/model assignments and per-project overrides.

```
Forge (.NET orchestrator, long-lived)
├── IssueStore / MemoryStore     task queue + dep graph + sprints + memory (SQLite or Azure SQL)
├── IssuesJsonlMirror            background tail -f mirror of the issue store
├── GitWorktreeService           per-task `git worktree add` lifecycle
├── GitHubService                PR open / status polling / merge
├── OrchestratorAgent            dispatch loop → 5-stage MAF workflow per task
├── SprintAssembler              completes Active sprint, assembles + activates the next (5-min tick)
├── MafAgentRunner               MAF ChatClientAgent + bash AIFunction + secret env injection
├── PRWatcher                    state-driven PR sweep: CI + review gate, rework rounds, merge
├── SlotTable                    per-(project, role) concurrency semaphores
└── DashboardHost                http://127.0.0.1:4097 (Kestrel + Blazor Server UI, Fluxor state)

External
├── LLM providers (HTTPS)        OpenAI-compatible or Anthropic-protocol endpoints
└── GitHub.com                   PRs, check runs, reviews, merges
```

## Prerequisites

1. **.NET 10 SDK** — `dotnet --version` should print `10.0.x`.
2. **At least one LLM provider key** — e.g. a kilo gateway JWT (see [`install-kilo.md`](install-kilo.md)), a MiniMax Token Plan key, or a Kimi key. Any endpoint speaking OpenAI chat-completions or Anthropic Messages works.
3. **GitHub PAT** with `repo` scope on each target repo — stored per project as the `github_token` secret (encrypted at rest); a global `GITHUB_TOKEN` is the fallback.
4. **Git** with worktree support.

## Build

```bash
dotnet build Forge.sln
```

`TreatWarningsAsErrors=true`; clean build expected.

## Test

```bash
dotnet test Forge.sln
```

Current suite: **1,200+ passing** (xUnit, hand-rolled fakes — no mocking frameworks; real-LLM tests are gated on configured provider keys). Highlights:

- `IssueStoreTests`, `IssueDepTests`, `IssuesJsonlMirrorTests` — store + dep graph + mirror
- `MemoryStoreTests`, `MemoryEndpointTests` — memory table + HTTP
- `BashToolTests`, `MafAgentRunnerBashToolTests` — MAF tool-call plumbing
- `ClaimExecutorTests` → `EnqueueWatchExecutorTests` — workflow executors
- `EngineeringDispatchWorkflowTests` — end-to-end workflow against a real temp git repo
- `OrchestratorAgentTests`, `PRWatcherTests`, `SprintAssemblerTests`, `ShellMutationClassifierTests` — integration

## Configuration

Copy `appsettings.example.json` to `appsettings.json` and fill in (gitignored). Top-level sections:

```jsonc
{
  "llm": {
    "defaultProvider": "kilo-gateway",
    "providers": [
      {
        "name": "kilo-gateway",
        "baseUrl": "https://api.kilo.ai/api/gateway",
        "apiKey": "KILO_GATEWAY_API_KEY",
        "defaultModel": "minimax/minimax-m3"
      }
      // Anthropic-protocol providers (Kimi, MiniMax direct, ...) add:
      //   "api": "anthropic", "auth": "bearer", "sharedQuota": true,
      //   "maxOutputTokens": 8192, "contextWindowTokens": 900000, "modelsUrl": "..."
    ],
    "overloadRetryCount": 3,
    "roles": {
      "CoreDev":   { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "ClientDev": { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "QA":        { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "Reviewer":  { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "Intake":    { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" }
    }
  },
  "github":    { "owner": "...", "repo": "...", "token": "GITHUB_TOKEN" },  // fallback only; per-project github_token secret wins
  "workspace": { "worktreeRoot": ".portHorizon/worktrees", "defaultBranch": "main" },
  "db":        { "provider": "sqlite" },  // or "sqlserver" + db.connectionString (Azure SQL)
  "spawner":   { "maxConcurrentSessions": 4, "pollIntervalSeconds": 3, "staleMinutes": 30 },
  "orchestrator": { "execution": "InProcess" },  // or "Durable" + dtsConnectionString
  "dashboard": { "enabled": true, "port": 4097, "hostname": "127.0.0.1" }
}
```

Notes:

- **Projects are DB-registered, not config-registered.** The `appsettings.json` `projects[]` array is deprecated and ignored at boot. Add projects via the dashboard **Projects** page or `POST /api/projects`; the registry row owns the repo URL, local clone path, default branch, and per-project role caps (`roles_json`).
- **Role model resolution order:** live DB override (`PUT /api/agents/roles/{name}/model`, project-scoped) → `llm.roles` config → provider default. Overrides apply without restart.
- Environment variables override any field (use `__` for nested keys), e.g. `GITHUB_TOKEN`, `Workspace__Root`, `db__connectionString`.

## CLI

```bash
# Long-running orchestrator + dashboard
dotnet run --project Forge

# Pre-flight: config + DB schemas + GitHub + LLM auth (exits non-zero on failure)
dotnet run --project Forge -- --check

# Print queue summary and exit
dotnet run --project Forge -- --status

# One dispatch cycle, then exit (cron / trigger-driven)
dotnet run --project Forge -- --once

# Dashboard only — host the UI without dispatching
dotnet run --project Forge -- --dashboard-only

# Dry-run recovery: see what StartupRecovery would do (no side-effects)
dotnet run --project Forge -- --recover

# Replay unfinished side-effects, then start dispatch
dotnet run --project Forge -- --recover-and-start

# Enqueue a task
dotnet run --project Forge -- \
  --enqueue-task "Add Position ECS component" \
  --task-type ecs --task-desc "..." --branch "agent/positions" [--task-id task-123]

# Worktree lifecycle smoke test against a real repo
dotnet run --project Forge -- --worktree-smoke

# One-shot SQLite -> Azure SQL state migration (stop the service first)
dotnet run --project Forge -- \
  --migrate-db --target sqlserver \
  --connection-string "Server=tcp:...;Initial Catalog=...;Authentication=Active Directory Default;" \
  [--include-open-work] [--reset]

# Provision the contained DB user (db_owner) for the forge-mi managed identity (idempotent)
dotnet run --project Forge -- --init-azure-sql [--mi-name forge-mi]

# Any mode accepts --config <path> to point at a specific appsettings file
dotnet run --project Forge -- --config /etc/forge/appsettings.json --check
```

See `docs/azure-sql-cutover.md` for the Azure SQL migration runbook.

## Dashboard

The orchestrator hosts a Blazor Server UI (Fluxor state management, SSE live updates) on `http://127.0.0.1:4097` (configurable via `dashboard.*`; optional multi-endpoint HTTP/HTTPS Kestrel binding is supported).

Pages (grouped as in the nav):

| Area | Pages |
|---|---|
| Overview | `/` Home, `/now` (unified cross-project admin view + alert inbox) |
| Planning | `/intake`, `/vision`, `/specs`, `/designs`, `/art`, `/backlog` |
| Execution | `/sprints`, `/tasks`, `/flow` (pipeline DAG + workflow edit mode), `/runs`, `/board` |
| Projects | `/projects`, `/projects/{id}/overview`, `/projects/{id}/secrets` |
| Operations | `/deployments`, `/agents` (agent control surface), `/skills`, `/ops/gates`, `/ops/recovery`, `/ops/cost`, `/ops/memory` |

The API surface includes `/api/state`, `/api/tasks/*`, `/api/sprints/*` (incl. propose/commit), `/api/gates*`, `/api/projects/*`, `/api/agents/*`, `/api/skills`, `/api/specs/*`, `/api/memory`, `/api/intake/*`, `/api/flow*`, `/api/workflow*` (draft/publish), `/api/agent-runs`, `/api/cost/*`, `/api/recovery/*`, `/api/events` (SSE), `/api/issues.jsonl`, plus health/meta endpoints. `GET /api/meta/endpoints` lists the live route table.

## How a task flows

1. **Intake.** New work enters as an epic via the Intake agent (operator chat at `/intake`) or as an ad-hoc task (CLI / dashboard / agent-filed follow-up).
2. **Grooming.** The pipeline schedulers (designer → groomer, 5-min ticks) refine specs into stories/tasks. Ad-hoc tasks must be marked `groomed=true` by the ScheduledGroomer before they are sprint-eligible.
3. **Sprint.** ALL engineering work happens inside a sprint. The `SprintAssembler` (5-min tick) completes the Active sprint when its tasks are terminal and assembles + activates the next from eligible Pending tasks. There is deliberately no UI button to create sprints.
4. **Dispatch.** The orchestrator claims a ready sprint task (`IssueStore.ClaimAsync` is atomic), acquires a per-(project, role) slot from `SlotTable`, and hands it to the 5-stage MAF workflow: **Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch**.
5. **Plan gate.** Before mutating, CoreDev/ClientDev runs must `submit_plan`; the bash tool refuses classified-mutating commands until deterministic gates (schema, territory) and the LLM plan-critic approve.
6. **Run.** `MafAgentRunner` builds the prompt (role instructions + memory recall + sprint context + task), injects per-project secrets as env vars (by reference — values never enter LLM context), and runs the MAF agent loop with the bash tool.
7. **PR.** The orchestrator commits, pushes `agent/<id>`, and opens the PR. Agents never open PRs themselves.
8. **Watch.** No separate watch row: the task IS the watch. The sweep (every 15 min) polls every live task with a `prNumber`. Merge requires green check runs AND an approval (formal review or reviewer-agent verdict) at the current head SHA. CI failure or changes-requested requeues the task for a rework round on the same branch/PR — circuit breaker at 3 attempts, then the task goes Blocked/Failed for the operator.
9. **Merge.** On green CI + approval (and with the `merge` stage gate released), the watcher merges, deletes the branch, removes the worktree, and marks the task Completed. Externally-merged PRs are detected too.
10. **Restart safety.** `StartupRecovery` runs at every startup, classifies every `InProgress + assignee=forge` issue by its `dispatch_checkpoint`, and replays unfinished side-effects (commit / push / PR open). Use `--recover` to dry-run. Opt-in P4 Stage B persists workflow state in a Durable Task Scheduler sidecar (`orchestrator.execution=Durable`). See `docs/p4-restart-safety.md`.

Optional: a dependency graph exists in `IssueStore` (`blocks` / `related` / `duplicates` edges). `ReadyAsync` excludes issues with an open `blocks` edge. `Failed` blockers are **not** auto-cleared — the operator must explicitly close them or remove the edge.

## Role agents

Nine slot pools, configured in `Agents/RoleAgentRegistry.cs`:

| Role | Territory | Tools | Purpose |
|---|---|---|---|
| `CoreDev`   | project backend dirs  | bash, read, edit, grep, glob, webfetch | Core engineering |
| `ClientDev` | project client/UI dirs | bash, read, edit, grep, glob, webfetch | Client/UI engineering |
| `QA`        | (read-only)            | bash, read, grep, glob | Build + test verification, no edits |
| `Reviewer`  | (read-only)            | read, grep, glob, webfetch | Architecture-compliance review on PRs |
| `Intake`    | (pipeline)             | chat + tool emits | operator inbox → proposed epic |
| `Designer`  | (pipeline)             | scheduler-side | spec → design |
| `Artist`    | (pipeline)             | scheduler-side | art/asset generation |
| `Groomer`   | (pipeline)             | scheduler-side | spec → stories/tasks; ad-hoc grooming |
| `Orchestrator` | (pipeline)          | no LLM | dispatch bookkeeping |

Role prompts resolve per project: `<projectRoot>/agents/<role>.md` wins (the project ships its own role instructions); otherwise the built-in copies next to the app are used. Engineering concurrency is per-role (default caps: coredev/clientdev/reviewer=2, others=1; live-tunable via `PUT /api/projects/{id}/roles`).

## State files

All persisted state lives under the project state root (`.portHorizon/state/` for the default project; `<dataRoot>/projects/{id}/.forge/state/` for registered projects; `/var/lib/forge/state/` under systemd):

```
state/
├── issues.db                 # IssueStore family (schema v31): tasks, deps, sprints, secrets, skills, agent runs
├── issues.jsonl              # IssuesJsonlMirror tail -f mirror (regenerated every 5s)
├── memory.db                 # MemoryStore
└── orchestrator-state.json   # heartbeat + counters (schema v3)
```

With `db.provider=sqlserver`, issue/memory data lives in Azure SQL instead (fresh-created at the current schema); the JSONL mirror still writes locally. The SQLite path migrates automatically on startup (`schema_version` row + migration chain); the orchestrator refuses to start on corrupt state or an unknown schema version.

## Logs

Structured single-line console logs (`journalctl -u forge -f` under systemd). Per-run agent diagnostics land in `<dataRoot>/logs/agent.log` (message roles, tool-call names, text lengths) — the first stop when a run "completes" with no diff. Set `Logging__LogLevel__Default=Debug` for verbose output (raw LLM requests, executor trace).

## Operational notes

- **Cross-process safety.** Run only one orchestrator per state directory. SQLite WAL allows concurrent readers; a second writer waits on the busy-timeout.
- **Concurrency.** Dev dispatch is bounded per (project, role) by `SlotTable`; `spawner.maxConcurrentSessions` no longer gates dev dispatch. A shared process-wide rate-limit tracker cools a provider+model for ALL subsystems on a 429, and a per-provider semaphore (`llm.maxConcurrentRequests`, default 2) caps simultaneous round-trips.
- **Stage gates.** Optional operator hold/release at four automatic transitions — `design`, `groom`, `sprint` (assembly only), `merge` — via `GET/POST /api/gates*` or the `/ops/gates` page. A held stage skips its scheduler tick; a held merge leaves the watch live.
- **Retries.** `StartupRecovery` replays unfinished side-effects at startup; after `MaxAttempts` (default 3) the issue is hard-failed. Inspect the audit row and run fresh sweeps from `/ops/recovery`.
- **Memory.** Project memory is injected into every agent prompt under "## Project memory" (`POST /api/memory` or `/ops/memory`).
- **Secrets.** Per-project encrypted secrets (`github_token`, `kilo_gateway_api_key`, custom kinds) are managed at `/projects/{id}/secrets` and injected by reference into agent bash env as `FORGE_SECRET_<KIND>`. Values never enter prompts, tool-call JSON, or logs.

## Architecture

The dispatch path is a MAF `WorkflowBuilder` graph in `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` — five typed `FunctionExecutor<TIn, TOut>` stages (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch). `OrchestratorAgent` claims the task and hands it to an `IWorkflowDispatcher`: InProcess (default) or Durable (DTS sidecar). The editable workflow definition (`/flow?mode=edit`) controls wiring and policy — gates, auto-merge, rework limits, step toggles — resolved per evaluation without restart; the task state transition table stays code-owned.

Module boundaries are non-negotiable: `Core/` has no I/O beyond the state DB, `Agents/` adds LLM + tools, `Orchestrator/` glues git + GitHub, `Dashboard/` + `Forge.UI/` render state. See `AGENTS.md` for the full rule layer and `docs/system-flow.md` for the narrative.

## Documentation

- **Guides:** [`docs/user-guide.md`](docs/user-guide.md) (operating Forge day-to-day) · [`docs/administrator-guide.md`](docs/administrator-guide.md) (install, config, deployment, security, ops)
- **Design & flow:** `docs/system-flow.md`, `docs/agent-framework-design.md`, `docs/embedded-issues.md`
- **Runbooks:** `docs/operator-cookbook.md`, `docs/linux-deployment.md`, `docs/azure-sql-cutover.md`, `docs/p4-restart-safety.md`, `docs/ui-troubleshooting.md`
- **Contributing:** `CONTRIBUTING.md`, `AGENTS.md`

## Out of scope (deferred)

- Per-task permission overrides beyond per-role
- Webhook-based PR watching (currently a 15-min sweep)
- Roslyn / NetArchTest architecture gates
- MCP playtest harness
- Godot headless smoke test
