# Forge

A long-lived .NET 10 orchestrator that drives AI coding agents (M3 by default) against any project that follows the conventions in `docs/agent-framework-design.md`. The first deployment targets the [Xyrces/PortHorizon](https://github.com/Xyrces/PortHorizon) Godot-ECS game repo, but `WorkspaceOptions` is fully configurable: pointing `workspace.root` at a different git repo + `github.owner`/`repo` at the corresponding GitHub project is the full deployment-time config. The orchestrator owns the task queue, git worktrees, GitHub PR lifecycle, and review-gated merge. **The model owns the code.**

The runtime uses the [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/overview/agent-framework-overview) 1.12.0 with [`Microsoft.Agents.AI.Workflows`](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows) for the dispatch pipeline. Agents are powered by the [kilo gateway](https://kilo.ai/docs/gateway) — an OpenAI-compatible HTTP endpoint. No separate `kilo serve` subprocess, no ACP, no per-session worktree cwd gymnastics.

```
Forge (.NET orchestrator, long-lived)
├── IssueStore (SQLite)         task queue + dep graph + memory + event log
├── MemoryStore (SQLite)         persistent project memory (bd remember/prime)
├── IssuesJsonlMirror            background tail -f mirror of the issue store
├── GitWorktreeService           per-task `git worktree add` lifecycle
├── GitHubService                PR open / status polling / merge
├── OrchestratorAgent            dispatch loop (claim → run → commit → PR)
├── MafAgentRunner               MAF ChatClientAgent + bash AIFunction
├── PRWatcher                    monitors PRs until CI+review gate passes
└── DashboardHost                http://127.0.0.1:4097 (Kestrel, static HTML)

External
├── kilo gateway (HTTPS)         LLM inference (OpenAI-compatible)
└── GitHub.com                   PRs, status checks, merges
```

## Prerequisites

1. **.NET 10 SDK** — `dotnet --version` should print `10.0.x`.
2. **kilo gateway API key** — see [`install-kilo.md`](install-kilo.md). JWT from <https://kilo.ai>.
3. **GitHub PAT** with `repo` scope on the target repo — `gh auth token` or <https://github.com/settings/tokens>.
4. **Git** with worktree support (any modern Git for Windows).

## Build

```bash
dotnet build Forge.sln
```

`TreatWarningsAsErrors=true`; clean build expected.

## Test

```bash
dotnet test Forge.sln
```

Current coverage: **402 passing, 2 skipped** (real-LLM tests gated on having a kilo gateway key configured). Test infrastructure includes:

- `IssueStoreTests`, `IssueDepTests`, `IssuesJsonlMirrorTests` — SQLite store
- `MemoryStoreTests`, `MemoryEndpointTests` — memory table + HTTP
- `BashToolTests`, `MafAgentRunnerBashToolTests` — MAF tool-call plumbing
- `ClaimExecutorTests` → `EnqueueWatchExecutorTests` — P3 workflow executors
- `EngineeringDispatchWorkflowTests` — end-to-end workflow against a real temp git repo
- `OrchestratorAgentTests`, `PRWatcherTests`, `RoleAgentRegistryTests` — integration

## Configuration

Copy `appsettings.example.json` to `appsettings.json` and fill in:

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
    ],
    "roles": {
      "CoreDev":   { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "ClientDev": { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "QA":        { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "Reviewer":  { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" },
      "Intake":    { "providerName": "kilo-gateway", "model": "minimax/minimax-m3" }
    }
  },
  "github":    { "owner": "Xyrces", "repo": "PortHorizon", "token": "GITHUB_TOKEN" },
  "workspace": { "root": "C:\\path\\to\\PortHorizon", "worktreeRoot": ".portHorizon\\worktrees", "defaultBranch": "main" },
  "spawner":   { "maxConcurrentSessions": 4, "pollIntervalSeconds": 3, "staleMinutes": 30 },
  "dashboard":  { "enabled": true, "port": 4097, "hostname": "127.0.0.1" }
}
```

Environment variables override any field (use `__` for nested keys):

| Var | Maps to |
|---|---|
| `KILO_GATEWAY_API_KEY` | `llm.providers[0].apiKey` |
| `KILO_MODEL` | `llm.providers[0].defaultModel` |
| `GITHUB_TOKEN` | `github.token` |
| `Workspace__Root` | `workspace.root` |

Use the env-var path for CI / shared hosts; use `appsettings.json` for local dev (the file is gitignored).

## CLI

```bash
# Long-running orchestrator + dashboard
dotnet run --project Forge

# One shot: process the queue once and exit
dotnet run --project Forge -- --once

# Dashboard only — host the UI without dispatching
dotnet run --project Forge -- --dashboard-only

# Print queue summary and exit
dotnet run --project Forge -- --status

# Pre-flight check: config + DB schemas + GitHub + kilo gateway auth
# (no dispatch; exits non-zero on any failure; useful for CI/smoke)
dotnet run --project Forge -- --check

# Dry-run recovery: see what StartupRecovery would do (no side-effects)
dotnet run --project Forge -- --recover

# Recovery + start: replay unfinished side-effects, then start dispatch
dotnet run --project Forge -- --recover-and-start

# Enqueue a task
dotnet run --project Forge -- \
  --enqueue-task "Add Position ECS component" \
  --task-type ecs \
  --task-desc "..." \
  --branch "agent/positions"
```

`--once` runs a single dispatch cycle and exits. Use it for cron-driven or trigger-driven dispatch. `--dashboard-only` is convenient for inspecting state without taking dispatch slots.

## Dashboard

The orchestrator hosts a local web UI on `http://127.0.0.1:4097` (configurable via `dashboard.*`).

Tabs:

| Tab | What it shows |
|---|---|
| **Tasks** | every issue in the store, filterable by status / type / assignee |
| **Spec** | specs + the Groomer agent for converting Approved specs into stories |
| **Intake** | OperatorAgent inbox, session list, AI-extracted tables |
| **Events** | live SSE stream of state transitions, agent runs, PR lifecycle |
| **Memory** | the `bd remember` / `prime` analog — list, add, delete persistent insights |

Endpoints:

| Path | Purpose |
|---|---|
| `GET /` (or `/index.html`) | the dashboard HTML page |
| `GET /api/state` | current task + agent + skill + sprint + heartbeat rollup |
| `GET /api/state/issues` | full issue list with metadata |
| `POST /api/state/issues` | enqueue a new task |
| `PATCH /api/state/issues/{id}` | transition status (e.g. set status=Failed) |
| `GET /api/state/issues/{id}/deps` | this issue's dependency edges + blocked flag |
| `POST /api/state/issues/{id}/deps` | add a blocks/related edge |
| `DELETE /api/state/issues/{id}/deps/{blockerId}/{kind}` | remove an edge |
| `GET /api/specs` | spec CRUD + version history |
| `POST /api/specs/{id}/groom` | trigger the GroomerAgent to decompose an Approved spec |
| `GET /api/memory[?prefix=...]` | list project memory (optionally filtered by key prefix) |
| `POST /api/memory` | add a memory |
| `DELETE /api/memory/{key}` | remove a memory |
| `GET /api/issues.jsonl` | stream the JSONL mirror of the issue store (`tail -f` equivalent) |
| `GET /api/issues.jsonl/path` | the absolute file path the mirror writes to |
| `GET /api/events` | SSE stream of `DashboardEvent` records (last ~1024 replayed on connect) |

The page polls `/api/state` every 2s and subscribes to `/api/events` for instant updates. The JSONL endpoint is safe to `curl` / `tail` from outside the orchestrator host.

## How a task flows

1. Operator enqueues a task (via the CLI, the dashboard, or `POST /api/state/issues`).
2. Orchestrator's `DispatchSingleTaskAsync` claims it (`IssueStore.ClaimAsync` is atomic — `Pending` → `InProgress`).
3. `GitWorktreeService` creates `agent/<id>` from `main` and a worktree at `.portHorizon/worktrees/<id>`.
4. The orchestrator builds a prompt: role's instructions + memory recall + task description + worktree path + operator-message-bus drain.
5. `MafAgentRunner` constructs a `ChatClientAgent` with the `bash` AIFunction and runs the MAF agent loop. The model emits structured `tool_calls`; MAF invokes bash, stdout flows back, the model iterates.
6. `MafAgentRunner` captures the model response in issue metadata (`modelResponse`).
7. `GitWorktreeService.CommitAllAsync` + `PushAsync` commit + push the branch.
8. `GitHubService.CreatePullRequestAsync` opens the PR (`[type] title`).
9. `OrchestratorAgent` enqueues a `pr-watch` follow-up issue.
10. `PRWatcher` polls GitHub every 30s. On green CI + approval, it merges, deletes the branch, removes the worktree, and marks the dev task `Completed`. On `REQUEST_CHANGES`, it marks `Blocked`. On red CI, it marks `Failed`.
11. **Restart safety**: P4 Stage A (default, in-process) — `StartupRecovery` runs at every startup, classifies every `InProgress + assignee=forge` issue by its `dispatch_checkpoint`, and replays unfinished side-effects (commit / push / PR open). Use `--recover` to dry-run. See `docs/p4-restart-safety.md` for the full contract + the audit row format. P4 Stage B (opt-in, requires Docker or Podman) persists the entire workflow state in a Durable Task Scheduler sidecar via `Microsoft.Agents.AI.DurableTask`. Bring up the sidecar with `docker compose -f deploy/docker-compose.yml up -d` (or `podman-compose ... up -d`) and set `Orchestrator:Execution=Durable`. See `deploy/README.md` for the full operation.

Optional: a `DependencyGraph` exists in `IssueStore` (`blocks` / `related` / `duplicates` edges). `ReadyAsync` excludes issues with an open `blocks` edge whose blocker is not `Completed`/`Closed`. `Failed` blockers are **not** auto-cleared — the operator must explicitly close them or remove the edge.

## Role agents

Configured in `Agents/RoleAgentRegistry.cs`:

| Role | Project scope | Tools (allowed) | Purpose |
|---|---|---|---|
| `CoreDev`   | `PortHorizon.Core/`   | bash, read, edit, grep, glob, webfetch | ECS components, systems, atmospherics, pathfinding |
| `ClientDev` | `PortHorizon.Client/` | bash, read, edit, grep, glob, webfetch | Godot 4.x scenes, scripts, UI, SyncBridge |
| `QA`        | (read-only)            | bash, read, grep, glob | Build + test verification, no edits |
| `Reviewer`  | (read-only)            | read, grep, glob, webfetch | Architecture-compliance review on GitHub |
| `Intake`    | (interactive)          | chat + tool emits | operator inbox → proposed spec + epic |

The system prompt for each role is loaded from `<workspace>/agents/<role>.md` (YAML frontmatter's `description:` field). Missing files get a generic fallback and a warning log.

## State files

Persisted under `.portHorizon/`:

```
.portHorizon/
├── state/
│   ├── issues.db              # IssueStore (SQLite, schema v7)
│   ├── issues.jsonl           # IssuesJsonlMirror tail -f mirror
│   ├── memory.db              # MemoryStore (SQLite, schema v7)
│   ├── orchestrator-state.json  # heartbeat + counters (schema v3)
│   └── *.jsonl (transient)    # schema-migration tmp files
└── worktrees/                  # one subdir per task
    └── <id>/                   # agent/<id> branch + checkout
```

The orchestrator refuses to start if any state file is corrupt or has an unknown schema version. The migration between schema versions is automatic on startup (the issue store reads the current `schema_version` row and applies any missing blocks).

## Logs

Structured single-line console logs:

```
13:42:01.102 info: Forge[0] Starting dashboard
13:42:01.731 info: Forge.Dashboard.DashboardHost[0] Dashboard listening on http://127.0.0.1:4097
13:42:01.750 info: Forge[0] Orchestrator starting
13:42:05.221 info: Forge.Orchestrator.OrchestratorAgent[0] Issue task-1 transition Pending -> InProgress (type=task)
13:42:48.901 info: Forge.Orchestrator.OrchestratorAgent[0] Agent session for task-1 completed in 43680ms
13:42:48.940 info: Forge.Orchestrator.OrchestratorAgent[0] Opened PR #42 for task-1
13:42:48.965 info: Forge.Orchestrator.OrchestratorAgent[0] Task task-1 dispatched to PR #42 (duration 43743ms)
```

Set `Logging__LogLevel__Default=Debug` for verbose output (raw LLM requests, executor trace).

## Operational notes

- **Cross-process safety.** The issue store uses SQLite WAL mode. Run only one orchestrator per state directory.
- **Concurrency.** `Spawner.MaxConcurrentSessions` (default 4) is the upper bound on simultaneous in-flight agent runs.
- **Retries.** `StartupRecovery` (P4 Stage A) replays unfinished side-effects at startup. After `StartupRecoveryOptions.MaxAttempts` (default 3) recoveries the issue is hard-failed. Use the dashboard's Recovery tab to inspect the audit row + run a fresh sweep. **P4 Stage B** (opt-in via `Orchestrator:Execution=Durable`) eliminates most retries entirely — workflow state persists in the DTS sidecar across orchestrator crashes.
- **Memory.** Project memory is injected into every agent prompt. Use `POST /api/memory` to add a key like `coding-style/no-linq-in-hot-paths` with a body. The agent sees the block under "## Project memory".
- **Spec → Groomer.** A spec with `status: Approved` can be decomposed into 1–3 stories × 1–3 tasks via `POST /api/specs/{id}/groom`. The GroomerAgent (also MAF) is fire-and-forget.

## Architecture

The dispatch loop is currently sequential (in `OrchestratorAgent.DispatchSingleTaskAsync`). A parallel MAF WorkflowBuilder implementation lives in `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` with the same five stages (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch) as typed `FunctionExecutor<TIn, TOut>` instances. The workflow version is exercised by `EngineeringDispatchWorkflowTests` against a real temp git repo; the orchestrator stays on the sequential code until behavioral parity on `AlreadyClaimed` / `NoDiff` short-circuits is fully verified.

For the broader design intent (dep graph, JSONL, memory, durable execution, …), see `docs/embedded-issues.md`, `docs/agent-framework-design.md`, and `docs/system-flow.md`.

## Out of scope (deferred)

- Per-task permission overrides beyond per-role
- Webhook-based `PRWatcher` (currently 30s polling)
- Roslyn / NetArchTest architecture gates
- MCP playtest harness
- Godot headless smoke test
- Wire `EngineeringDispatchWorkflow` into `OrchestratorAgent` (architectural, not functional)
- Durable execution via `Microsoft.Agents.AI.Hosting` (P4 in `agent-framework-design.md`)

For the operator cookbook (common scenarios) and the system flow diagram, see `docs/operator-cookbook.md` and `docs/system-flow.md`.
