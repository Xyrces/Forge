# PortHorizon.Agents

A long-lived .NET orchestrator that drives Kilo agents over the Agent Client Protocol (ACP) to build [Xyrces/PortHorizon](https://github.com/Xyrces/PortHorizon). The orchestrator owns the task queue, git worktrees, GitHub PR lifecycle, and review-gated merge; **Kilo owns the code**.

```
PortHorizon.Agents (.NET orchestrator, long-lived)
├── AcpProcessManager       starts/stops `kilo acp --port 4096`
├── AcpClient (StreamJsonRpc)  JSON-RPC client to ACP per task
├── RoleAgentRegistry        maps AgentType → Kilo agent name + role config
├── GitWorktreeService       per-task `git worktree add` lifecycle
├── GitHubService            PR open / status polling / merge
├── OrchestratorAgent        queue + dispatch loop
└── PRWatcher                polls PR state, triggers merge on approve+green

External (Kilo)
├── `kilo acp` server         JSON-RPC over TCP, one per orchestrator
├── `kilo github` App         webhook-driven PR reviewer on PortHorizon repo
└── Kilo agent definitions    .kilo/agents/{coredev,clientdev,qa,reviewer}.md
```

## Prerequisites

1. **.NET 10 SDK** — `dotnet --version` should print `10.0.x`.
2. **kilo CLI** — see [`install-kilo.md`](install-kilo.md) for the host checklist.
3. **Kilo GitHub App installed** on `Xyrces/PortHorizon` — see [`docs/install-kilo-github.md`](docs/install-kilo-github.md).
4. **Git** with worktree support (any modern Git for Windows).

## Build

```bash
dotnet build PortHorizon.Agents.sln
```

`TreatWarningsAsErrors=true`; clean build expected.

## Test

```bash
dotnet test PortHorizon.Agents.sln
```

Minimum viable coverage:

- `StateStoreTests` — round-trip, corrupt JSON, schema-version rejection, atomic write.
- `StateReaperTests` — stale-task sweep, retry-budget exhaustion, no-op cases.
- `PRWatcherTests` — full CI × review verdict table.
- `RoleAgentRegistryTests` — task-type → role mapping and tool permissions.

## Configuration

Copy `appsettings.example.json` to `appsettings.json` and fill in:

```jsonc
{
  "kilo": { "provider": "kilocode", "model": "kilocode/minimax-m3", "orgId": "" },
  "github": { "owner": "Xyrces", "repo": "PortHorizon", "token": "" },
  "workspace": { "root": "C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon", "worktreeRoot": ".portHorizon\\worktrees", "defaultBranch": "main" },
  "acpServer": { "executablePath": "kilo", "port": 4096, "hostname": "127.0.0.1" },
  "spawner": { "maxConcurrentSessions": 4, "pollIntervalSeconds": 3, "staleMinutes": 30 }
}
```

Environment variables override any field (see `Configuration/OptionsLoader.cs`):

| Var | Maps to |
|---|---|
| `KILO_PROVIDER`, `KILO_MODEL`, `KILO_ORG_ID` | `kilo.*` |
| `GitHub__Token`, `GITHUB_TOKEN` | `github.token` |
| `Workspace__Root`, etc. (double-underscore for nested keys) | `workspace.*` |

## CLI

```bash
# Long-running orchestrator (default; Ctrl+C to stop)
dotnet run --project PortHorizon.Agents

# One shot: process the queue once and exit
dotnet run --project PortHorizon.Agents -- --once

# Dashboard only — host the UI without starting ACP or the orchestrator
dotnet run --project PortHorizon.Agents -- --dashboard-only

# Print queue summary and exit
dotnet run --project PortHorizon.Agents -- --status

# Enqueue a task without editing the state file by hand
dotnet run --project PortHorizon.Agents -- \
  --enqueue-task t-123 \
  --task-type ecs \
  --task-desc "Add Position ECS component to PortHorizon.Core"
```

`--once` runs a single dispatch cycle and exits — useful for cron-driven or trigger-driven dispatch.

## Dashboard

The orchestrator hosts a local read-only web UI on `http://127.0.0.1:4097` (configurable via `dashboard.*`). It shows:

- **Task table** — id, type, status, branch, role agent, PR number, last update, error.
- **Status counts** — pending / in-progress / completed / failed / blocked.
- **Live event log** — Server-Sent Events stream of state transitions, ACP session lifecycle, PR opened/merged/changes-requested/failed.

Endpoints:

| Path | Purpose |
|---|---|
| `GET /` (or `/index.html`) | The dashboard HTML page |
| `GET /api/state` | Current `OrchestratorState` as JSON |
| `GET /api/agents` | Registered role agents and their tool permissions |
| `GET /api/events` | SSE stream of `DashboardEvent` records (kind = `task.transition`, `acp.session.*`, `pr.*`, `log`) |

The page polls `/api/state` every 2 s and subscribes to `/api/events` for instant updates. The SSE stream replays the last ~1024 events on connect so reconnecting clients don't lose context.

## How a task flows

1. Operator enqueues a task (via `--enqueue-task`, or by editing `.portHorizon/state/orchestrator-state.json`).
2. Orchestrator picks it up on its next poll cycle (`Spawner.PollIntervalSeconds`, default 3 s).
3. Orchestrator takes one slot from the `MaxConcurrentSessions` semaphore (default 4).
4. `GitWorktreeService` creates `agent/<taskId>` from `main` and a worktree at `.portHorizon/worktrees/<taskId>`.
5. `AcpProcessManager` opens a new ACP `session/new` (cwd = worktree path, agent name = role).
6. Orchestrator prompts the role agent with the task description and the role's rules.
7. Kilo edits files, runs `dotnet build`, runs tests, commits, pushes the branch.
8. Orchestrator opens a GitHub PR (head `agent/<taskId>` → `main`) and enqueues a `pr-watch` follow-up.
9. `kilo github` App reviews the PR (approve or request changes).
10. `PRWatcher` polls GitHub every 30 s. On green CI + approval, it merges, deletes the branch, removes the worktree, and marks the dev task `Completed`. On `REQUEST_CHANGES`, it marks the dev task `Blocked`. On red CI, it marks `Failed`.
11. Stale `InProgress` tasks are reaped at startup (`Spawner.StaleMinutes`, default 30 m); one retry is permitted before `Failed`.

## Role agents

Defined as Kilo custom-mode files in `.kilo/agents/`:

| File | Project scope | Tools | Purpose |
|---|---|---|---|
| `coredev.md` | `PortHorizon.Core/` | bash, read, edit, grep, glob, webfetch | ECS components, systems, atmospherics, pathfinding |
| `clientdev.md` | `PortHorizon.Client/` | bash, read, edit, grep, glob, webfetch | Godot 4.x scenes, scripts, UI, SyncBridge |
| `qa.md` | (read-only) | bash, read, grep, glob | Build + test verification, no edits |
| `reviewer.md` | (read-only) | read, grep, glob, webfetch | Architecture-compliance review on GitHub |

Register them locally with:

```bash
./scripts/install-agents.sh    # bash
pwsh ./scripts/install-agents.ps1    # PowerShell
```

## State files

Persisted under `.portHorizon/`:

```
.portHorizon/
├── state/
│   ├── orchestrator-state.json    # task queue (SchemaVersion = 2)
│   └── heartbeat-<agentId>.json   # per-agent heartbeats
└── worktrees/                     # one subdir per task
```

The orchestrator refuses to start if the state file is corrupt (`StateCorruptException`) or has an unknown schema version (`StateSchemaException`). Delete or migrate the file to recover.

## Logs

Structured single-line console logs:

```
13:42:01.102 [INFO] Starting ACP server: kilo acp --port 4096 ...
13:42:01.731 [INFO] ACP server: kilo-cli v0.7.4 (proto=1)
13:42:01.750 [INFO] Orchestrator starting
13:42:05.221 [INFO] Task t-123 transition Pending -> InProgress (agent=CoreDev)
13:42:48.901 [INFO] ACP session for t-123 completed in 43680ms
13:42:48.940 [INFO] Opened PR #42 for t-123
13:42:48.965 [INFO] Task t-123 dispatched to PR #42 (duration 43743ms)
```

Set `Logging__LogLevel__Default=Debug` for verbose output (poll internals, raw JSON-RPC payloads).

## Operational notes

- **Cross-process safety.** The state store uses an in-process `SemaphoreSlim`. Run only one orchestrator per state directory.
- **Windows path quirks.** All `git` invocations centralize path composition in `GitWorktreeService`. Path with spaces (e.g. `C:\Users\jtn50\repos\gamedev\PortHorizon`) are quoted at the single point of use.
- **Kilo ACP per-session cwd.** The orchestrator passes the worktree path on `session/new`. If your kilo version rejects the per-session cwd, the orchestrator's `AcpProcessManager` is the only thing to change — call sites use `AcpClient.NewSessionAsync(cwd, agent)` so a per-task process model is a one-method swap.
- **Stale tasks.** A `InProgress` task whose `UpdatedAt` is older than `Spawner.StaleMinutes` is reset to `Pending` (one retry) or `Failed` (budget exhausted) on the next orchestrator start.

## Out of scope (deferred)

- Per-task permission overrides beyond per-role
- Webhook-based PRWatcher (currently 30 s polling)
- Roslyn / NetArchTest architecture gates
- MCP playtest harness
- Godot headless smoke test
- SQLite-backed state store with cross-process locking
- Task dependency DAG
- Automated `kilo github install`