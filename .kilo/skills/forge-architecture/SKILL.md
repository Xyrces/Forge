---
name: forge-architecture
description: Forge system architecture and navigation map. Use when an agent needs to locate where a responsibility lives in the repo, what module boundaries are non-negotiable, or what the high-level data + dispatch shape looks like. Source of truth for "where is X?" in Forge.
---

# forge-architecture

One-paragraph summary: Forge is a long-lived .NET 10 orchestrator that drives AI coding agents (M3 by default, via the kilo gateway) against one or more registered target projects. It is **project-agnostic** — projects live in the SQLite `project` table (DB-only registry; `appsettings.json projects[]` is deprecated) and each project gets its own SQLite file, git clone, worktree root, and dispatch bundle. It owns the task queue (SQLite IssueStore per project), git worktrees, GitHub PR lifecycle, and review-gated merge. Agents are powered by Microsoft Agent Framework (MAF) `ChatClientAgent` + `bash` AIFunction; the runtime also has Durable Task Scheduler (DTS) integration as P4 Stage B. Runs under systemd (user-mode or system) on Linux; see `docs/linux-deployment.md`. See `README.md` for the full intro; this skill is the navigation map.

## Module map

| Module | Responsibility | Notes |
|---|---|---|
| `Core/` | Domain types + stores | No HTTP / no LLM / no env-var reading. SQLite stores take paths via constructor. |
| `Core/IssueStore.cs` | The task queue + dep graph + event log + memory + recovery checkpoints + `project` / `secret` tables | Schema currently **v19**. Heart of the system; everything else hangs off this. |
| `Core/ProjectStore.cs` | Project registry CRUD (DB-only source of truth) | `roles_json` holds per-project role caps (v19). |
| `Core/SecretStore.cs` | Per-project encrypted secrets (IDataProtector, `forge.secret.v1`) | Kinds: `github_token`, `kilo_gateway_api_key`, `meshy_api_key` + custom. Keyring: `~/.aspnet/DataProtection-Keys/`; rotation invalidates all secrets. |
| `Core/MemoryStore.cs` | Persistent project memory (the `bd remember` analog) | Prime-injected into every agent prompt. |
| `Core/SpecStore.cs`, `DesignArtifactStore.cs`, `ArtOutputStore.cs` | Spec / design / art rows for the Intake→Engineering pipeline | SQLite tables; per-spec artifacts and runs. |
| `Core/DispatchCheckpoint.cs` | The six recovery checkpoints (`claimed` → `pr_opened`) | P4 Stage A reads this to know what to replay. |
| `Core/IssuesJsonlMirror.cs` | Tail-able mirror of IssueStore | Rewritten every 5s. Viewer artifact; IssueStore wins. |
| `Projects/` | Project-agnostic plumbing: `ProjectCloner` (HTTPS+PAT clone, credential file), `ProjectBootstrap` (clone-or-scaffold), `ProjectContext`/`ProjectContextFactory` (per-project store bundles for the dashboard) | Hot-reload: `POST /api/projects` shows up without restart. |
| `Agents/MafAgentRunner.cs` | Wraps `ChatClientAgent`, builds prompt (role + skills + memory), wires the `bash` AIFunction + secrets-by-reference env | Single chokepoint for all MAF runs. Injects `FORGE_SECRET_*` / `GITHUB_TOKEN` env into BashTool when `context["projectId"]` is set. |
| `Agents/RoleAgentRegistry.cs` | Maps `AgentType` → role name + ProjectSubdir + allowed tools | Plus `FromTaskType` for the issue-type → role mapping. |
| `Agents/IntakeAgent.cs` | Operator → Spec (Draft) | MAF session per project; persisted via `IIntakeStore`. |
| `Agents/ProductAgent.cs` | Refines an intake-draft spec into a structured body | Writes spec_version rows with `author="product:<run_id>"`. |
| `Agents/DesignerAgent.cs` | Spec(Draft|ReadyForDesign) → Spec(Designed/NeedsRevision) | 6 AIFunctions; runs through `DesignHygieneChecker` first. |
| `Agents/ArtistAgent.cs` | Spec(Designed) → Spec(AssetReady/NeedsRevision) | 6 AIFunctions; drives `MeshyClient` for text/image-to-3D. |
| `Agents/GroomerAgent.cs` | Approved spec → stories + tasks | Reads `Approved` specs (also `Designed | AssetReady | Groomed`). |
| `Agents/SkillBootstrap.cs` | Idempotently seeds the godot-ecs-gamedev-playbook into MemoryStore under `playbook/*` | Operator edits are preserved across restarts. |
| `AgentTools/BashTool.cs` | `/bin/sh -c <command>` AIFunction with timeout (`cmd.exe /c` on Windows) | Default `workingDirectory` is the task's worktree; optional `envVars` ctor param injects secrets-by-reference. |
| `Orchestrator/OrchestratorAgent.cs` | The dispatch loop (production path) | Iterates registered projects, builds/caches a `ProjectDispatchBundle` per project, claims issues, delegates to `IWorkflowDispatcher`. |
| `Orchestrator/ProjectDispatchBundle.cs` | Per-project bundle: IssueStore + stores + GitWorktreeService + GitHubService + PRWatcher | `ProjectDispatchBundleFactory.Build()`; per-project `github_token` secret overrides the global GitHub PAT. |
| `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` + `*Executor.cs` | The five-stage MAF Workflow (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch) | **Live** — this IS the production dispatch path (via `InProcessDispatcher`). Note: executors are currently constructed with the primary project's stores at startup; the per-claim multi-project loop is in `OrchestratorAgent`, per-store workflow wiring is a known follow-up. |
| `Orchestrator/PRWatcher.cs` | Polls PRs every 30s; merges on green CI + approval; marks Blocked on REQUEST_CHANGES, Failed on red CI | Also handles branch cleanup + worktree removal on merge. |
| `Orchestrator/StartupRecovery.cs` | P4 Stage A in-process crash recovery | Reads `dispatch_checkpoint`, replays unfinished side-effects. |
| `Orchestrator/DurableDispatcher.cs` | P4 Stage B (opt-in via `Orchestrator:Execution=Durable`) | DTS-backed workflow runtime. |
| `Orchestrator/DesignerAgent.cs`, `ArtistAgent.cs`, `ScheduledGroomer.cs`, `MemoryExtractor.cs` | Pipeline schedulers | Background IHostedService-style tickers. |
| `Dashboard/` | Kestrel HTTP host (80/443, self-signed cert, setcap) + minimal-API endpoints + Blazor UI (`Forge.UI/`) | Reads stores; publishes/subscribes `DashboardEvent` via `IDashboardEventBus`. Global project switcher in the topbar scopes most reads via `?projectId=`. `/cert` + `/cert/install` for cert trust. |
| `Configuration/` | `appsettings.json` shape + env-var binder | Only `Dashboard/` and `Agents/` consume it (not `Core/`). `ProjectRegistryLoader` reads the DB registry. |
| `Reviewer/` | Reserved for future designer/artist roles | Empty today. |
| `deploy/` + `scripts/` | systemd unit + install scripts | `deploy/systemd/forge.service`, `scripts/install-systemd-service.sh`, `scripts/forge-setcap-setup.sh`. |
| `docs/` | Design docs + vision status + operator cookbook + per-phase references | `system-flow.md`, `vision-status.md`, `linux-deployment.md`, `agent-framework-design.md`, `operator-cookbook.md`, `p4-restart-safety.md`, etc. |

## State storage map

All state lives under a single **data root** (systemd: `/var/lib/forge/state/`; user-mode: `~/.local/share/forge/state/`). Per-project files live under `<dataRoot>/projects/<id>/`.

| Concern | Storage | Access pattern |
|---|---|---|
| project registry + role caps | `<dataRoot>/state/issues.db` `project` table (roles_json, v19) | `Core/ProjectStore.cs`; DB-only source of truth |
| per-project secrets | each project's issues.db `secret` table (v18) | IDataProtector ciphertext; `Core/SecretStore.cs`; never returned as plaintext over HTTP |
| task queue + dep graph | `<dataRoot>/projects/<id>/state/issues.db` (SQLite, per project) | WAL, concurrent readers, single writer orchestrator |
| persistent memory | `memory.db` (SQLite) | Prime-injected into every agent prompt |
| visual design | `design_artifact` + `designer_run` (SQLite) | per-spec artifacts, run timeline |
| produced art | `art_output` + `artist_run` (SQLite) | per-spec `.glb`/`.png`/`.mp4` paths + Meshy task list |
| spec state machine | `spec` + `spec_status` (SQLite) | Draft → ReadyForDesign → Designed → AssetReady → ReadyForGroom → Grooming → Groomed → Shipped |
| dispatch checkpoints + recovery | `issue.dispatch_checkpoint` + `recovery_report` (SQLite) | each engineering executor advances BEFORE its side-effect; StartupRecovery replays unfinished ones |
| durable workflow runtime | DTS sidecar (`deploy/docker-compose.yml`) | opt-in via `Orchestrator:Execution=Durable`; persists workflow state across orchestrator crashes |
| event log | `issue_event` (SQLite) + `IssueStore.AddEventAsync` | appended per transition, queryable by dashboard |
| heartbeat + counters | `orchestrator-state.json` (StateStore) | small JSON, view-only |
| tail-able mirror | `issues.jsonl` (IssuesJsonlMirror) | rewritten every 5s, atomic rename |
| git worktrees | `<dataRoot>/worktrees/<project>/<id>/` | one per task; cleaned up by `GitWorktreeService.RemoveAsync` on merge |
| git credentials | `<clone>/.forge/git-credentials` (mode 0600) | written by ProjectCloner; PAT never persists in `origin` URL |
| branches | target git repo | one `agent/<id>` per task; deleted on merge |
| produced art files | `<dataRoot>/art-output/{spec}/{art-id}.{ext}` | relative path stored in `art_output.body`; served at `/api/art-output/{id}/file` |
| TLS cert | `~/.config/forge/certs/forge.pfx` (+ `.crt`) | self-signed, SANs: hostname/localhost/127.0.0.1/LAN IP; `/cert` + `/cert/install` endpoints distribute it |
| PRs | github.com/<owner>/<repo> per project | `PRWatcher` polls; merged or closed per review |

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
- `.kilo/skills/forge-secrets/SKILL.md` — the per-project secrets system: storage, UI, by-reference consumption.
