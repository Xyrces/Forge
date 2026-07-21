---
name: forge-recovery
description: Forge restart-safety — P4 Stage A in-process StartupRecovery (the default) vs P4 Stage B Durable Task Scheduler sidecar (opt-in via Orchestrator:Execution=Durable). Use when reasoning about crashes, the dispatch_checkpoint column, recovery reports, or durability tradeoffs.
---

# forge-recovery

Forge has two restart-safety mechanisms for engineering-dispatch work. They're not interchangeable: Stage A is on by default and runs in-process; Stage B is opt-in and persists workflow state in a sidecar.

## The six checkpoints

The engineering dispatch pipeline writes `issue.dispatch_checkpoint` at every stage boundary. `Core/DispatchCheckpoint.cs` enumerates them; the recoverer reads them to know what side-effects still need to be replayed:

| Value (DB) | Enum | Side-effect already done | Replay from |
|---|---|---|---|
| `claimed` | `Claimed` | status=InProgress, assignee=forge | Worktree acquisition |
| `worktree_acquired` | `WorktreeAcquired` | + `worktreePath` + `branch` set in metadata | Agent run |
| `agent_completed` | `AgentCompleted` | + `modelResponse` set in metadata | Commit |
| `commit_done` | `CommitDone` | + `branchSha` updated | Push |
| `push_done` | `PushDone` | (no extra metadata) | PR open |
| `pr_opened` | `PrOpened` | + `prNumber` set | (no replay — `PRWatcher` path takes over) |

The convention in every executor is "advance the checkpoint **before** the side-effect", so on a crash the recoverer knows whether the side-effect happened or not (if the checkpoint was set, the side-effect is assumed durable; otherwise it must be replayed).

## P4 Stage A — StartupRecovery (default, in-process)

`Orchestrator/StartupRecovery.cs`.

- Runs at every orchestrator startup.
- Classifies every `InProgress + assignee=forge` issue by its `dispatch_checkpoint`.
- Replays unfinished side-effects (commit / push / PR open).
- Writes a `recovery_report` row at the end of the pass. `recovery_report` table holds:
  - `ts`, `spec_id?`, `issues_scanned`, `issues_replayed`, `issues_failed`, `duration_ms`.
  - `actions_json` is an array of `RecoveryActionRecord { IssueId, BeforeCheckpoint, AfterCheckpoint, Action, Error }` where `Action ∈ { "replay", "failed", "left_alone", "already_recovered" }`.

### CLI / API surface

| Flag / endpoint | Behavior |
|---|---|
| `--recover` | dry-run: see what `StartupRecovery` would do. No side-effects. |
| `--recover-and-start` | replay unfinished side-effects, then start dispatch. |
| `POST /api/recovery/run` | same as `--recover-and-start` (HTTP). |
| `POST /api/recovery/dry-run` | same as `--recover` (HTTP). |
| `GET /api/recovery/reports` | list of past reports (most recent first). |

### Hard-fail rule

After `StartupRecoveryOptions.MaxAttempts` (default 3) recoveries for a single issue, the issue is hard-failed. The operator must intervene via the dashboard's Recovery tab (inspect the audit row, fix the underlying cause, force a fresh sweep).

### What Stage A does NOT cover

- Designer / Artist / Groomer schedulers — they use fresh MAF agents per run, not the engineering workflow. Stage A does not replay their state.
- Long-running open `pr-watch` issues — those are owned by `PRWatcher` polling, not the engineering workflow.

## P4 Stage B — Durable Task Scheduler (opt-in)

`Orchestrator/DurableDispatcher.cs` + `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` + `deploy/docker-compose.yml`.

- Opt-in via `Orchestrator:Execution=Durable` in appsettings.json / env vars. Default is `InProcess`.
- The workflow definition is unchanged — `ConfigureDurableWorkflows` registers the existing `EngineeringDispatchWorkflow` with the DTS sidecar.
- Sidecar: DTS emulator image `mcr.microsoft.com/dts/dts-emulator:latest`, ports 8080/8082. Both `docker compose` and `podman-compose` work (`deploy/docker-compose.yml`).
- When Stage B is on, the orchestrator's workflow state persists across crashes in the DTS sidecar. `StartupRecovery` becomes largely redundant for engineering-dispatch work (it still runs, but should find nothing to do).
- `IWorkflowDispatcher` abstracts over InProcess vs Durable; the dispatcher is the same code path at the call site (`OrchestratorAgent.DispatchSingleTaskAsync` → `_dispatcher.DispatchAsync`).

### Tradeoffs

| Concern | Stage A | Stage B |
|---|---|---|
| Operational cost | None — pure in-process | Sidecar container to bring up + monitor |
| Crash window | Anything between two checkpoint writes can be lost on crash (small — milliseconds) | Workflow state persists across crashes |
| Test surface | `StartupRecovery`-specific unit tests + 3 kill-restart tests in `p4-restart-safety.md` | `Microsoft.Agents.AI.DurableTask` integration tests |
| Migration cost | Zero — it's on by default | Requires `Orchestrator:Execution=Durable` switch flip + sidecar |
| When to recommend | Operator runs the orchestrator on a single host, accepts restart-time replay | Multi-host / HA / strict "no replay ever" |

### Webhook-driven PR merge signals

Scoped to Stage B but deferred (per `docs/vision-status.md`). The existing 30s `PRWatcher` poll is fine for Stage B's needs.

## Decision rubric

- Operator says "task-123 crashed and never got its PR" → Stage A. Run `dotnet run --project Forge -- --recover-and-start` or check `GET /api/recovery/reports`.
- Operator says "I want engineering dispatch to survive orchestrator process crashes across hosts" → suggest Stage B. Point at `deploy/README.md` and the `Orchestrator:Execution=Durable` switch.
- Operator says "I need to inspect what StartupRecovery did last time" → `GET /api/recovery/reports` or the dashboard Recovery tab.
- Code touches `Orchestrator/StartupRecovery.cs` or the `dispatch_checkpoint` column → mirror changes in `Core/DispatchCheckpoint.cs`, the workflow executors, and `tests/Forge.Tests/RecoveryReport*Tests`.

## Files in this area

- `Core/IssueStore.cs` — schema for `dispatch_checkpoint` + `recovery_report` + `recovery_attempts` columns.
- `Core/DispatchCheckpoint.cs` — enum + DB string helpers + `RecoveryActionRecord` / `RecoveryReportRecord`.
- `Core/RecoveryReportStore.cs` — typed accessor over the audit table.
- `Orchestrator/StartupRecovery.cs` — Stage A implementation.
- `Orchestrator/DurableDispatcher.cs` + `IWorkflowDispatcher.cs` — Stage B dispatcher abstraction.
- `Orchestrator/Workflow/*Executor.cs` — each executor advances the checkpoint before its side-effect.
- `Dashboard/RecoveryEndpoints.cs` — `GET /api/recovery/reports`, `POST /api/recovery/run`, `POST /api/recovery/dry-run`.
- `deploy/docker-compose.yml` — DTS sidecar.
- `docs/p4-restart-safety.md` — full P4 contract + audit row format + the 3 kill-restart verification tests.
