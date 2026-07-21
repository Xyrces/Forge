---
name: forge-architecture
description: Forge system architecture and navigation map. Use when an agent needs to locate where a responsibility lives in the repo, what module boundaries are non-negotiable, or what the high-level data + dispatch shape looks like. Source of truth for "where is X?" in Forge.
---

# forge-architecture

One-paragraph summary: Forge is a long-lived .NET 10 orchestrator that drives AI coding agents (M3 by default, via the kilo gateway) against a target project (PortHorizon — a Godot/ECS game — by default). It owns the task queue (SQLite IssueStore), git worktrees, GitHub PR lifecycle, and review-gated merge. Agents are powered by Microsoft Agent Framework (MAF) `ChatClientAgent` + `bash` AIFunction; the runtime also has Durable Task Scheduler (DTS) integration as P4 Stage B. See `README.md` for the full intro; this skill is the navigation map.

## Module map

| Module | Responsibility | Notes |
|---|---|---|
| `Core/` | Domain types + stores | No HTTP / no LLM / no env-var reading. SQLite stores take paths via constructor. |
| `Core/IssueStore.cs` | The task queue + dep graph + event log + memory + recovery checkpoints | Schema currently v15. Heart of the system; everything else hangs off this. |
| `Core/MemoryStore.cs` | Persistent project memory (the `bd remember` analog) | Prime-injected into every agent prompt. |
| `Core/SpecStore.cs`, `DesignArtifactStore.cs`, `ArtOutputStore.cs` | Spec / design / art rows for the Intake→Engineering pipeline | SQLite tables; per-spec artifacts and runs. |
| `Core/DispatchCheckpoint.cs` | The six recovery checkpoints (`claimed` → `pr_opened`) | P4 Stage A reads this to know what to replay. |
| `Core/IssuesJsonlMirror.cs` | Tail-able mirror of IssueStore | Rewritten every 5s. Viewer artifact; IssueStore wins. |
| `Agents/MafAgentRunner.cs` | Wraps `ChatClientAgent`, builds prompt (role + skills + memory), wires the `bash` AIFunction | Single chokepoint for all MAF runs. |
| `Agents/RoleAgentRegistry.cs` | Maps `AgentType` → role name + ProjectSubdir + allowed tools | Plus `FromTaskType` for the issue-type → role mapping. |
| `Agents/IntakeAgent.cs` | Operator → Spec (Draft) | MAF session per project; persisted via `IIntakeStore`. |
| `Agents/ProductAgent.cs` | Refines an intake-draft spec into a structured body | Writes spec_version rows with `author="product:<run_id>"`. |
| `Agents/DesignerAgent.cs` | Spec(Draft|ReadyForDesign) → Spec(Designed/NeedsRevision) | 6 AIFunctions; runs through `DesignHygieneChecker` first. |
| `Agents/ArtistAgent.cs` | Spec(Designed) → Spec(AssetReady/NeedsRevision) | 6 AIFunctions; drives `MeshyClient` for text/image-to-3D. |
| `Agents/GroomerAgent.cs` | Approved spec → stories + tasks | Reads `Approved` specs (also `Designed | AssetReady | Groomed`). |
| `Agents/SkillBootstrap.cs` | Idempotently seeds the godot-ecs-gamedev-playbook into MemoryStore under `playbook/*` | Operator edits are preserved across restarts. |
| `AgentTools/BashTool.cs` | `cmd.exe /c <command>` AIFunction with timeout | Default `workingDirectory` is the task's worktree. |
| `Orchestrator/OrchestratorAgent.cs` | The dispatch loop (production path) | Sequential code; calls `IWorkflowDispatcher` so Stage B can swap in DTS. |
| `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` + `*Executor.cs` | MAF Workflows parallel implementation (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch) | Dormant; tested but not wired as the production path. |
| `Orchestrator/PRWatcher.cs` | Polls PRs every 30s; merges on green CI + approval; marks Blocked on REQUEST_CHANGES, Failed on red CI | Also handles branch cleanup + worktree removal on merge. |
| `Orchestrator/StartupRecovery.cs` | P4 Stage A in-process crash recovery | Reads `dispatch_checkpoint`, replays unfinished side-effects. |
| `Orchestrator/DurableDispatcher.cs` | P4 Stage B (opt-in via `Orchestrator:Execution=Durable`) | DTS-backed workflow runtime. |
| `Orchestrator/DesignerAgent.cs`, `ArtistAgent.cs`, `ScheduledGroomer.cs`, `MemoryExtractor.cs` | Pipeline schedulers | Background IHostedService-style tickers. |
| `Dashboard/` | Kestrel HTTP host + minimal-API endpoints + `wwwroot/index.html` SPA | Reads stores; publishes/subscribes `DashboardEvent` via `IDashboardEventBus`. One endpoint file per concern. |
| `Configuration/` | `appsettings.json` shape + env-var binder | Only `Dashboard/` and `Agents/` consume it (not `Core/`). |
| `Reviewer/` | Reserved for future designer/artist roles | Empty today. |
| `deploy/docker-compose.yml` | DTS sidecar for P4 Stage B | `mcr.microsoft.com/dts/dts-emulator:latest`; `docker compose` and `podman-compose` both work. |
| `docs/` | Design docs + vision status + operator cookbook + per-phase references | `system-flow.md`, `vision-status.md`, `agent-framework-design.md`, `operator-cookbook.md`, `p4-restart-safety.md`, `embedded-issues.md`, `intake-to-sprint-workflow.md`, etc. |

## State storage map

| Concern | Storage | Access pattern |
|---|---|---|
| task queue + dep graph | `.portHorizon/state/issues.db` (SQLite) | WAL, concurrent readers, single writer orchestrator |
| persistent memory | `.portHorizon/state/memory.db` (SQLite) | Prime-injected into every agent prompt |
| visual design | `design_artifact` + `designer_run` (SQLite) | per-spec artifacts, run timeline |
| produced art | `art_output` + `artist_run` (SQLite) | per-spec `.glb`/`.png`/`.mp4` paths + Meshy task list |
| spec state machine | `spec` + `spec_status` (SQLite) | Draft → ReadyForDesign → Designed → AssetReady → ReadyForGroom → Grooming → Groomed → Shipped |
| dispatch checkpoints + recovery | `issue.dispatch_checkpoint` + `recovery_report` (SQLite) | each engineering executor advances BEFORE its side-effect; StartupRecovery replays unfinished ones |
| durable workflow runtime | DTS sidecar (`deploy/docker-compose.yml`) | opt-in via `Orchestrator:Execution=Durable`; persists workflow state across orchestrator crashes |
| event log | `issue_event` (SQLite) + `IssueStore.AddEventAsync` | appended per transition, queryable by dashboard |
| heartbeat + counters | `orchestrator-state.json` (StateStore) | small JSON, view-only |
| tail-able mirror | `issues.jsonl` (IssuesJsonlMirror) | rewritten every 5s, atomic rename |
| git worktrees | `.portHorizon/worktrees/<id>/` | one per task; cleaned up by `GitWorktreeService.RemoveAsync` on merge |
| branches | target git repo | one `agent/<id>` per task; deleted on merge |
| produced art files | `.portHorizon/art-output/{spec}/{art-id}.{ext}` | relative path stored in `art_output.body`; served at `/api/art-output/{id}/file` |
| PRs | github.com/Xyrces/PortHorizon | `PRWatcher` polls; merged or closed per review |

## Reading order for newcomers

1. `docs/system-flow.md` — what runs when a task is dispatched
2. `docs/vision-status.md` — which phase of `agent-framework-design.md` is live
3. `docs/operator-cookbook.md` — how an operator uses it
4. `Program.cs` — the CLI entry point
5. `Core/IssueStore.cs` — the heart of the system
6. `Orchestrator/OrchestratorAgent.cs::DispatchSingleTaskAsync` — the dispatch loop
7. `Agents/MafAgentRunner.cs` — how the agent runs
8. `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` — the MAF-Workflows version (dormant)
9. `Dashboard/DashboardHost.cs` — how the HTTP surface composes
10. `tests/Forge.Tests/IssueStoreTests.cs` — best entry point into the test suite

## Companion skills

- `.kilo/skills/forge-task-lifecycle/SKILL.md` — the engineering dispatch pipeline (single task end-to-end).
- `.kilo/skills/forge-recovery/SKILL.md` — P4 Stage A in-process recovery vs P4 Stage B DTS sidecar.
