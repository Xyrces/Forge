# Forge — Master Design

> Status: **current as of 2026-08-09** — every claim in this document
> is grounded in the actual code at this commit on `agent/task-203`.
> The file is the path the dashboard's Vision tab renders
> (`Dashboard/VisionEndpoints.cs::MapVisionEndpoints` → `GET /api/vision`)
> and the content the operator's `PUT /api/vision` writes back; both
> reads and writes refresh the `vision/master` memory key so every
> subsequent agent prompt includes this document under the
> `## Project memory` block in `Agents/MafAgentRunner.cs::BuildSkillInstructionsAsync`
> (memory recall path) and `MemoryStore.RecallAsync`.
>
> See `Dashboard/VisionStore.cs::VisionStore` for the loader.
> Cross-checked against `tests/Forge.Tests/VisionEndpointTests.cs` and
> `tests/Forge.Tests/VisionEndpointWriteTests.cs`.

## 1. What this system is and who it serves

**Forge** is a long-lived .NET 10 orchestrator (single binary, `dotnet
run --project Forge`) that drives AI coding agents against one or more
**registered projects** in a developer-team-shaped loop. The agent's
job is to take an issue, edit code in a per-task git worktree, push a
branch, and open a PR; the orchestrator's job is to own the queue, the
worktree, the GitHub PR lifecycle, the review-gated merge, and the
restart-safety story around all of it.

- **Operator** (a single human) is the *product owner*. They file the
  intake, approve specs, add memory, gate stages, steer the system
  from the dashboard, and ship.
- **Agents** are the *scrum team*. Five pipeline roles today (Intake,
  Product, Designer, Artist, Groomer) feed work into the four
  engineering roles (CoreDev, ClientDev, QA, Reviewer — see
  `Agents/RoleAgentRegistry.cs::RoleAgentRegistry`). The orchestrator
  owns the engineering dispatch loop.
- **The model owns the code.** The orchestrator's only contract with
  the LLM is a single `IAgentRunner` seam
  (`Agents/IAgentRunner.cs`); the MAF runner
  (`Agents/MafAgentRunner.cs`) is the implementation.

The first deployment target is
[Xyrces/PortHorizon](https://github.com/Xyrces/PortHorizon), a Godot-ECS
game; the first meta-goal is "build Forge with Forge" — register a
project entry pointing at `Xyrces/Forge.git` and let the orchestrator
dispatch engineering work against its own repo (see
`appsettings.example.json` and the project model note in
`AGENTS.md § "Project model (DB-only registry, project-agnostic)"`).

**Operator-visible surfaces:**

- Kestrel dashboard at `http://127.0.0.1:4097` (`Dashboard/DashboardHost.cs`).
  The dashboard ships in two layers: a minimal-API HTTP surface (the
  `Dashboard/*Endpoints.cs` files) and a Blazor Server UI in
  `Forge.UI/` (per `Forge.Core/Forge.Core.csproj` `ProjectReference`
  to `Forge.UI/Forge.UI.csproj`). Pages include AppShell (home),
  Tasks, Backlog, Specs, Designs, Art, Intake, Sprints, Agents,
  Skills, Vision, Projects, Board, Ops (Recovery, Memory,
  Cost/Headroom).
- JSONL mirror (`Core/IssuesJsonlMirror.cs`) at
  `<dataRoot>/state/issues.jsonl`, rewritten every 5 s, atomic via
  `temp + rename`. It is a *viewer artifact only* — the SQLite
  `IssueStore` is the source of truth (`AGENTS.md § "The JSONL
  mirror is a viewer artifact"`).
- SSE event stream at `GET /api/events`
  (`Dashboard/DashboardHost.cs:535`).
- CLI flags documented in `README.md § "CLI"` and parsed in
  `Program.cs::ParseMode`. The flag surface is a contract — adding
  escape hatches without operator sign-off is forbidden
  (`AGENTS.md § "Don't add --dashboard-only-style escape-hatch CLI
  flags"`).

**Test baseline (this commit):** `dotnet test Forge.sln --nologo`
reports **1056 passed, 2 skipped, 0 failed**. The two skipped tests
are `RealLlmIntegrationTests` (gated on a configured kilo gateway
key, per the README test infra note).

## 2. Module map and non-negotiable boundaries

The codebase's structure is enforced by the four rules in
`AGENTS.md § "Module boundaries (non-negotiable)"` (lifted from
`CONTRIBUTING.md § "Boundaries"`). Each rule is also a code smell
test: if a class violates one, it should be split.

| Module | Responsibility | Hard rule | Key paths |
|---|---|---|---|
| `Core/` | Domain types + stores. The heart of the system. | No I/O beyond the state database (SQLite or Azure SQL via `Core/Db/`). No HTTP, no GitHub, no LLM. Stores take paths via constructor; they do not read env vars or config. | `Core/IssueStore.cs`, `Core/MemoryStore.cs`, `Core/SpecStore.cs`, `Core/SecretStore.cs`, `Core/SprintStore.cs`, `Core/ProjectStore.cs`, `Core/AgentRunStore.cs`, `Core/IssuesJsonlMirror.cs`, `Core/DispatchCheckpoint.cs`, `Core/TaskState.cs`, `Core/TaskStateMachine.cs`, `Core/StageGates.cs`, `Core/ModelRateLimitTracker.cs`, `Core/SkillStore.cs` |
| `Agents/` | MAF agent implementations + runner. | Depends on `Core/`, `Configuration/`, `Dashboard/`. Publishes `DashboardEvent`. Does **not** read `appsettings.json` directly. | `Agents/MafAgentRunner.cs`, `Agents/RoleAgentRegistry.cs`, `Agents/IChatClientFactory.cs`, `Agents/RateLimitAwareChatClient.cs`, `Agents/IntakeAgent.cs`, `Agents/ProductAgent.cs`, `Agents/GroomerAgent.cs`, `Agents/GroomerAgentFactory.cs`, `Agents/IAgentRunner.cs`, `Agents/Gates/*` |
| `Orchestrator/` | Dispatch loop + recovery + sprint assembler + design/art schedulers + memory extraction. | Depends on `Agents/` + `Core/`. Glues stores + agent + git + GitHub. | `Orchestrator/OrchestratorAgent.cs`, `Orchestrator/StartupRecovery.cs`, `Orchestrator/ProjectDispatchBundle.cs`, `Orchestrator/MemoryExtractor.cs`, `Orchestrator/Sprint/SprintAssembler.cs`, `Orchestrator/Sprint/SprintProposeService.cs`, `Orchestrator/DesignerScheduler.cs`, `Orchestrator/ArtistScheduler.cs`, `Orchestrator/ScheduledGroomer.cs`, `Orchestrator/Slots/SlotTable.cs`, `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` + the 5 executors, `Orchestrator/DurableDispatcher.cs` |
| `Dashboard/` | Kestrel + minimal-API endpoints + Blazor UI. | Depends on `Core/` + `Configuration/`. Reads stores; publishes/subscribes events via `IDashboardEventBus`. | `Dashboard/DashboardHost.cs`, `Dashboard/*Endpoints.cs` (one file per concern), `Dashboard/VisionStore.cs`, `Dashboard/VisionEndpoints.cs`, `Dashboard/Flow/FlowGraph.cs`, `Dashboard/Flow/DirectedFlowLayout.cs`, `Dashboard/UIExtensions.cs`; the Blazor app lives in `Forge.UI/` |
| `Configuration/` | `appsettings.json` shape + env-var binder. | Only consumed by `Dashboard/`, `Agents/`, and `Orchestrator/`. | `Configuration/AgentOptions.cs`, `Configuration/OptionsLoader.cs`, `Configuration/ProjectRegistryLoader.cs`, `Configuration/ForgesystemPaths.cs`, `Configuration/GateOptions.cs`, `Configuration/DeploymentOptions.cs`, `Configuration/DbOptions.cs` |
| `Projects/` | Project-agnostic plumbing (cloning, contexts, bootstrap). | The orchestrator is **project-agnostic** — projects live in the SQLite `project` table (`schema v17+`); `appsettings.json projects[]` is a deprecated seed. | `Projects/ProjectCloner.cs`, `Projects/ProjectBootstrap.cs`, `Projects/ProjectContext.cs`, `Projects/GitHubTokenResolver.cs` |
| `AgentTools/` | `AIFunction` implementations exposed to the LLM. | Bound to the role's tool allowlist in `RoleAgent.AllowedTools`. | `AgentTools/BashTool.cs`, `AgentTools/GitWorktreeService.cs`, `AgentTools/GitRefNames.cs`, `AgentTools/SubmitPlanTool.cs`, `AgentTools/FollowUpTool.cs`, `AgentTools/ShellMutationClassifier.cs`, `AgentTools/ArtifactReadTool.cs`, `AgentTools/RunVerification.cs` |
| `Reviewer/` | Reviewer/PR-watcher role. | Empty `Reviewer/` directory reserved by `CONTRIBUTING.md` for future designer/artist roles; currently the reviewer surface lives under `Reviewer/PRWatcher.cs` and `Reviewer/ReviewerDispatcher.cs`. | `Reviewer/PRWatcher.cs`, `Reviewer/ReviewerDispatcher.cs` |
| `Meshy/` | Meshy REST client (text-to-3D, image-to-3D, rigging). | Used by the Artist agent (`Orchestrator/ArtistAgent.cs`) for visual asset generation. | `Meshy/MeshyClient.cs` |
| `DeploymentPipeline/` | Self-deploy from a merged commit to the running orchestrator. | Merges do NOT auto-deploy; the operator must explicitly approve a deployment candidate. | `DeploymentPipeline/DeploymentStore.cs`, `DeploymentPipeline/DeploymentBuildRunner.cs`, `DeploymentPipeline/SelfHostedSystemdServiceDeploymentExecutor.cs` (Linux) |
| `tests/Forge.Tests/` | xUnit + integration tests. | Test project allows warnings (the main project is `TreatWarningsAsErrors=true`); no Moq — hand-rolled typed fakes only (`CONTRIBUTING.md § "Things to avoid"`). | `tests/Forge.Tests/IssueStoreTests.cs`, `tests/Forge.Tests/VisionEndpointTests.cs`, `tests/Forge.Tests/VisionEndpointWriteTests.cs`, `tests/Forge.Tests/Integration/*.cs` |

**The rule, restated** (`AGENTS.md § "Module boundaries"`): "If a
class needs to read `IOptions<X>` AND write `IssueStore` AND make
HTTP calls, that is a code smell. Split it across Core / Agents /
Orchestrator."

## 3. Core data + dispatch flows

### 3.1 Storage model

**Source of truth:** a single SQLite database per project
(`<dataRoot>/projects/<id>/state/issues.db`, schema **v25** per
`Core/IssueStore.cs::CurrentSchemaVersion`). The SQLite migration
chain is in `Core/IssueStore.cs::InitializeSchemaSqlite`; the SQL
Server fresh-create is in `Core/IssueStore.cs::InitializeSchemaSqlServer`
with ordered migrations under `Core/Db/Migrations/`. The dual-provider
seam lives in `Core/Db/` (`Core/Db/ForgeDb.cs` + `Dialect`).

**Tables in `issues.db`** (creation order from `InitializeSchemaSqlite`,
all guarded by `CREATE TABLE IF NOT EXISTS` for re-run idempotency):

- **Queue + dependencies:** `issue`, `issue_seq`, `issue_dep`,
  `issue_event`, `memory`, `agent`, `agent_run`, `skill`,
  `sprint`, `sprint_issue`.
- **Intake:** `intake_session`, `intake_message`.
- **Spec pipeline:** `spec`, `spec_version`, `spec_diagram`,
  `spec_touches`, `spec_dep`, `codebase_graph_cache`.
- **Groomer:** `issue_groomer_run`.
- **Designer:** `design_artifact`, `designer_run`.
- **Artist:** `art_output`, `artist_run`.
- **Recovery:** `recovery_report`.
- **Context lineage:** `context_handoff`, `memory_extraction`.
- **Secrets (per-project, encrypted via `IDataProtector`,
  purpose `forge.secret.v1`):** `secret`.
- **Project registry (global, schema v17+):** `project` with
  `roles_json` (v19) for per-project role caps.
- **Schema bookkeeping:** `schema_version`.

The dual-provider story is governed by `Core/Db/Dialect.cs`; every
query uses `@` parameters via `DbCommandExtensions.AddParam` and
`Dialect.Table(name)` (per `CONTRIBUTING.md § "Schema migrations"`).

**Configuration state:**

- `MemoryStore` (`Core/MemoryStore.cs`) — keyed insights (`bd remember`
  analog), TTL-aware, prime-injected into every agent prompt as the
  `## Project memory` block.
- `StateStore` (`StateStore.cs`) — heartbeat + counters (schema v3),
  view-only for the dashboard.
- `IssuesJsonlMirror` (`Core/IssuesJsonlMirror.cs`) — tail-able
  rewrite every 5 s, atomic via temp + rename. Viewer artifact only.
- `ForgesystemOptions` (`Configuration/ForgesystemOptions.cs`) +
  `ForgesystemPaths.cs` — data root resolution (systemd: `/var/lib/forge/state/`;
  user-mode: `~/.local/share/forge/state/`; Windows: `%LOCALAPPDATA%\Forge`).

### 3.2 Issue lifecycle (the central state machine)

Two layers of state:

1. **`IssueStatus`** (`Core/IssueStore.cs::IssueStatus`) — the
   authoritative queue state: `Pending`, `InProgress`, `Completed`,
   `Failed`, `Blocked`, `Closed`. Transitions are validated by
   `IssueStore.ClaimAsync` (atomic), `IssueStore.TransitionAsync`,
   and `Core/TaskStateMachine.cs::TaskStateMachine` (operator rule
   2026-07-26 — illegal transitions logged as errors and flagged in
   metadata, never thrown).
2. **`TaskLifecycleState`** (`Core/TaskState.cs::TaskLifecycleState`)
   — the derived lifecycle state with operator-facing `WaitingOn`
   + `Strikes`. Computed by `TaskStateProjector.Derive` from the
   task row, the PR metadata, and whether a dev run is currently
   active. Used by the Flow page, the Task detail, and the reaper
   decisions.

**Status state machine:**

- `Pending` → `InProgress` is **atomic** via
  `IssueStore.ClaimAsync(id, assignee, ct)`
  (`Core/IssueStore.cs:1687`). The transaction wraps `IsBlockedAsync`
  + the `UPDATE` + the `issue_event` insert so two dispatchers on
  the same DB cannot both claim a freshly-unblocked task.
- `InProgress` → `Completed | Failed | Blocked | Closed` via
  `IssueStore.TransitionAsync` (`Core/IssueStore.cs:1731`).
- `Failed` is intentionally **not** auto-cleared
  (`AGENTS.md § "Don't auto-clear Failed issues"`). The operator must
  close it or remove the blocking edge. `ReadyAsync`'s
  `notBlockedPredicate` excludes `blocks` edges whose blocker is
  NOT in `(Completed, Closed)` — `Failed` is open-on-purpose.
- `Closed` is the operator's explicit terminal verdict
  (`PATCH /api/state/issues/{id} {"status":"Closed"}`).

**Container vs dispatchable:** `Core/IAgent.cs::AgentTaskTypes.IsContainer`
classifies `type ∈ {epic, story}` as containers — the engineering
dispatch loop never claims them.

### 3.3 Dependency graph semantics

`issue_dep(kind)` has three values (`Core/IssueStore.cs::IssueDepKind`):

| Kind | Behavior | Enforcement |
|---|---|---|
| `blocks` | `ReadyAsync` excludes the issue if the blocker is not `Completed`/`Closed`. | Hard (dispatcher query) |
| `related` | Informational; shown in the dashboard graph | None |
| `duplicates` | Informational | None |

Self-loops are rejected. Cycle detection is by operator inspection —
the orchestrator is single-writer so cycles are rare and self-resolving.

### 3.4 Engineering dispatch flow (production path)

The single task end-to-end. Production is
`Orchestrator/OrchestratorAgent.cs::DispatchSingleTaskAsync`, which
calls `IssueStore.ClaimAsync` up-front and then hands off to
`Orchestrator/IWorkflowDispatcher.DispatchAsync`. The default
implementation is `InProcessDispatcher`, which builds
`Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` per dispatch
and runs it via MAF `InProcessExecution` — **the workflow executors
ARE the production path**. With `Orchestrator:Execution=Durable`
(opt-in), the same workflow runs on the Durable Task Scheduler
sidecar (`Orchestrator/DurableDispatcher.cs`); see § 6.

The five-stage pipeline:

```
[Claim] → [Worktree] → [RunAgent] → [CommitPushPr] → [EnqueueWatch] → [PRWatcher]
```

Each stage is a `FunctionExecutor<TIn, TOut>`
(`Orchestrator/Workflow/*Executor.cs`); the typed TIn/TOut generics
declare `AlreadyClaimed` / `NoDiff` / `Skipped` as first-class
result variants so the runtime can short-circuit without
conditional edges.

**Stage 1 — Claim** (`ClaimExecutor`). The dispatcher's
`ClaimExecutor` short-circuits when the input is already
`InProgress + assignee=forge` (P3 final-wiring behavior) and
otherwise claims itself. Production path claims up-front and lets
the dispatcher's executor pass through. Sets
`dispatch_checkpoint=claimed` BEFORE the worktree side-effect.

**Stage 2 — Worktree** (`WorktreeExecutor`). Creates branch
`agent/<id>` from `workspace.defaultBranch` (e.g. `main`) and a
worktree at `<workspace.worktreeRoot>/<id>/` — default
`.portHorizon/worktrees/<id>`. Sets metadata `worktreePath` +
`branch`. Advances `dispatch_checkpoint=worktree_acquired` BEFORE
the directory exists on disk.

**Stage 3 — RunAgent** (`RunAgentExecutor` +
`Agents/MafAgentRunner.cs::RunAsync`). Builds and runs the MAF
agent. System instructions are assembled in this order (see
`Agents/MafAgentRunner.cs:135-149`):

1. **Role instructions** — `description:` frontmatter from
   `<workspace>/agents/<role>.md` (where `<role>` is `RoleAgent.AgentName`, e.g.
   `coredev`). Per-project role prompts (schema v24): a project
   that ships its own `agents/<role>.md` wins (per
   `AGENTS.md § "Skills are per-project and dual-owned (schema v24)"`).
2. **Project skills block** — appended via `BuildSkillInstructionsAsync`
   from `ISkillSource` (currently `Agents/SqliteSkillSource.cs`);
   falls back to empty on load error.
3. **Project memory block** — `## Project memory` from
   `MemoryStore.RecallAsync()`; rendered as a bullet list with
   expiry metadata. Falls back to empty on error.

The user's prompt (the operator's task body + worktree context) goes
to the user message — **never** to instructions. This is the P1 fix
(`Agents/MafAgentRunner.cs:149`).

Tools wired into the agent:

- `BashTool(workingDirectory=<worktree>, envVars=<project secrets>)`
  AIFunction — `/bin/sh -c <command>` on Linux/macOS, `cmd.exe /c`
  on Windows (`AgentTools/BashTool.cs`).
- `ArtifactReadTool` (`AgentTools/ArtifactReadTool.cs`) — pulls a
  single artifact body on demand.
- `SubmitPlanTool` (`AgentTools/SubmitPlanTool.cs`) — gates
  mutating bash commands per the plan-gate contract.

**Secrets by reference:** when `context["projectId"]` is set,
`MafAgentRunner.ResolveSecretEnvAsync` decrypts the project's stored
secrets and injects them into the bash process environment: every
kind as `FORGE_SECRET_<KIND>` (uppercased, `-`→`_`), plus
`github_token` as `GITHUB_TOKEN`. Values never enter the model's
prompt, tool-call JSON, or logs — the model references `$VAR` names
only. `Core/SecretStore.cs::SecretKinds` enumerates the known kinds
(`github_token`, `kilo_gateway_api_key`, `meshy_api_key`,
`kimi_api_key`); custom kinds are allowed (regex
`[a-z0-9][a-z0-9_-]{0,63}`).

LLM client built via `IChatClientFactory.Create(_config, role)` —
resolves the provider/model from `LlmConfig.Resolve(role)`. Provider
config in `Agents/LlmConfig.cs`. Wrapped with
`ChatClientBuilder.UseFunctionInvocation()` so model-emitted
`FunctionCallContent` actually runs the tool. Rate-limit-aware
wrapper `Agents/RateLimitAwareChatClient.cs` shares a single
`Core/ModelRateLimitTracker.cs` instance process-wide so a 429 from
ANY subsystem (dev run, groomer, designer, reviewer sweep, intake,
memory extractor) cools the model for ALL of them, with
`Retry-After` honored when present.

After the run, capture the assistant text into `modelResponse`
metadata (truncated to 2000 chars). Returns an `AgentRunResult
{ Text, SessionId, InputTokens, OutputTokens, Elapsed }`. Advances
`dispatch_checkpoint=agent_completed` after `modelResponse` is
captured.

**Plan gate** (`Agents/Gates/RunGatePipeline.cs`,
hard-enforced at the tool layer). Per the operator rule
2026-07-26 ("hard gates wherever quality doesn't suffer"):

- `PlanSchemaGate` (Deterministic) — required sections
  (goal/files/approach/test/done), no LLM.
- `PlanTerritoryGate` (Deterministic) — file paths must match the
  role's `TerritoryPrefixes` (per-project `roles_json` overrides
  win; built-in registry territory otherwise).
- `PlanLlmReviewGate` (Llm) — small reviewer-model critic; bounded
  by a 2-min timeout, fails OPEN on outage (deterministic gates
  already filtered structure + territory).

Resolution per checkpoint: DB override (memory key
`gates/run/<checkpoint>`) → `gates.run.<checkpoint>` config →
built-in defaults (`Agents/Gates/RunGatePipeline.cs::ResolveWithSourceAsync`).
Mechanical rework rounds (conflict sync, infra retrigger) fast-path
auto-approve. Revision budget 2, then the run fails structured.
The audit trail lands in task metadata `planGate` (rendered on the
TaskDetail page). `ShellMutationClassifier`
(`AgentTools/ShellMutationClassifier.cs`) refuses mutating bash
commands until the plan is approved.

**Stage 4 — CommitPushPr** (`CommitPushPrExecutor`). Sequence:

1. `GitWorktreeService.CommitAllAsync` — commits everything in the
   worktree with message `Task(<id>): <title>`.
2. **NoDiff short-circuit:** if no files changed, transition the
   issue to `Completed` with message `"no changes (agent made 0
   edits)"` and return without pushing / opening a PR.
3. `GitWorktreeService.PushAsync` — pushes `agent/<id>` to origin.
4. `GitHubService.CreatePullRequestAsync` — opens the PR
   (`[type] title`, body from `BuildPrBody`).
5. Sets metadata: `prNumber`, `branchSha`. Transitions issue to
   `Completed`.

Advances `dispatch_checkpoint` through `commit_done` →
`push_done` → `pr_opened` (each BEFORE the side-effect).

**Stage 5 — EnqueueWatch** (`EnqueueWatchExecutor`). The state
contract changed in 2026-07-29: `pr-watch` is retired as a separate
issue type; the **task IS the watch**. The
`prNumber`/`branch`/`worktreePath` metadata + the lifecycle states
on the task row are everything the watcher needs, so the sweep
polls every live (Pending|InProgress) task with a `prNumber`. The
`EnqueueWatch` workflow stage is now a graph placeholder (creates
nothing); legacy pr-watch rows still in a queue are Closed by the
sweep ("superseded") as it discovers them. Legacy pr-watch rows
still in flight are reconciled by the sweep.

### 3.5 Sprint flow

**Sprint is the fundamental execution model** (`AGENTS.md § "Sprint
flow"` and `Orchestrator/Sprint/SprintAssembler.cs`):

- All engineering work happens inside a sprint.
- `SprintAssembler` (5-min tick per project) completes the Active
  sprint when every member task is terminal, then assembles +
  activates the next from eligible Pending tasks.
- Assembly is deterministic: eligible tasks are grouped by their
  root epic (task → story → spec via `parent_issue_id`) and the
  oldest epic's group becomes the next sprint (name = epic title,
  goal = epic description). Parentless tasks fall into an "Ad-hoc
  work" group. Stories are linked too (progress display);
  completion counts non-container tasks only.
- Ad-hoc (parentless) work has exactly two paths, never a grab-bag
  (operator rule 2026-07-27): (a) **injection** into the ACTIVE
  sprint when it belongs there — its `followUpOf` chain reaches a
  sprint member (same work), a `blocks` dep edge has it blocking
  a sprint member, operator P1 / `blocker=true`, or operator
  requeue (`requeuedFromFailedAt`); or (b) a **solo sprint** —
  unrelated groomed ad-hoc tasks assemble one-per-sprint.
- `OrchestratorAgent` gates dispatch: no Active sprint → no dev
  dispatch; otherwise only sprint members pass the filter
  (`Core/SprintStore.cs::GetIssueIdsAsync`).
- The watch sweep is exempt (it's lifecycle, not sprint work, and
  discovers watched tasks by `prNumber` metadata).
- Sprint flow deliberately has no UI button to create sprints
  (`AGENTS.md § "Sprint flow"`).
- Agent runs inside a sprint carry shared context: `RunAgentExecutor`
  adds `sprintId/sprintName/sprintGoal/sprintRoster` to the run
  context; `MafAgentRunner` renders a `## Sprint` block + recalls
  `sprint/{sprintId}/` memory keys before global project memory.
- `MemoryExtractor` dual-persists extracted memories under
  `sprint/{sprintId}/` when the issue is in the Active sprint.

### 3.6 Cross-project routing (planning-lane discipline)

The primary store is **not** a global queue — every issue row a
pipeline stage creates belongs to the store OWNED by the work's
project (operator rule 2026-07-29, after the live misrouting
incident):

- Groomer writes stories/tasks to `spec.ProjectId`'s store via
  `GroomerAgentFactory.Create(projectId:)` + `issueStoreLookup`.
- Intake writes epics to the session project's store.
- The ad-hoc groomer sweeps every registered project's queue
  (`ScheduledGroomer` + `ProjectContextFactory`).

`SprintAssembler.DropCrossProjectGroupsAsync` is the
defense-in-depth guard: it refuses to assemble tasks whose spec is
owned by another project and logs an error.

### 3.7 Multi-project registry

Project registry is **DB-only**, with `appsettings.json projects[]`
deprecated as a seed (operator rule in `AGENTS.md § "Project model"`).
On first boot `Configuration/ProjectRegistryLoader.cs::SeedAsync`
seeds projects from config (idempotent). The registry table is
`project` in `state/issues.db` (schema v17); v18 added per-project
`secret`; v19 added `project.roles_json` for DB-persisted role
caps.

`Projects/ProjectCloner.cs` clones HTTPS+PAT (PAT injected into
clone URL only, then stripped from the stored remote; a
credential-store file is written for future `git push` /
`git pull`). `Projects/ProjectBootstrap.cs` falls back to a local
`git init` scaffold when no repo URL is set. Each project gets:

- A local clone at `<dataRoot>/projects/<id>/`.
- Its own `state/issues.db`.
- A `GitHubService` + `GitWorktreeService` in a cached
  `Orchestrator/ProjectDispatchBundle` (`OrchestratorAgent` builds
  per-project bundles via `ProjectDispatchBundleFactory.Build`).

### 3.8 Secrets-by-reference

The design goal: **agents and runtime code can USE a secret without
the value ever entering the LLM's context window** (prompt,
tool-call JSON, or logs).

Storage: table `secret` in each project's issues SQLite file
(schema v18). Encryption: `IDataProtector`, purpose string
`forge.secret.v1` (`Core/SecretStore.cs`). Keyring:
`~/.aspnet/DataProtection-Keys/`. **Rotating the keyring invalidates
every stored secret** — `GetPlaintextAsync` returns null on decrypt
failure and consumers fall back to global config.

HTTP rule: plaintext is NEVER returned over the API. The list
endpoint returns `(kind, set, createdAt, updatedAt)` only.
Dashboard's Secrets page (`Forge.UI/Components/Pages/Secrets.razor`)
is the editor; the per-project secret override for `github_token`
wins in `ProjectDispatchBundleFactory` over the global GitHub PAT.

By-reference consumption: see § 3.4 Stage 3.

## 4. Subsystem deep-dives

### 4.1 Pipeline stages

The Intake → Product → Designer → Artist → Groomer → Sprint →
Engineering pipeline (per `docs/system-flow.md § "Pipeline"`):

| Stage | Component | Scheduler / trigger | Output |
|---|---|---|---|
| Intake | `Agents/IntakeAgent.cs`, `Core/IntakeStore.cs` (schema v3) | Persistent `IntakeAgentRegistry` per project; operator UI in Intake tab | `intake_session` + `intake_message` + epics (issue type `epic`) |
| Product | `Agents/ProductAgent.cs` | Per-epic refinement; writes `spec_version` rows with `author="product:<run_id>"` | Spec with `SpecStatus.Draft` (refines Draft → ReadyForDesign) |
| Design | `Orchestrator/DesignerAgent.cs`, `Core/DesignArtifactStore.cs` (schema v9), `Orchestrator/DesignerScheduler.cs` (5-min tick) | `POST /api/specs/{id}/design` (manual) | `design_artifact` rows; spec → `Designed` / `Approved` (non-visual fast-path) / `NeedsRevision` |
| Art | `Orchestrator/ArtistAgent.cs`, `Core/ArtOutputStore.cs` (schema v10), `Meshy/MeshyClient.cs`, `Orchestrator/ArtistScheduler.cs` | `POST /api/specs/{id}/design-art` (manual) | `art_output` rows + downloaded `.glb`/`.png`/`.mp4`; spec → `AssetReady` |
| Groomer | `Agents/GroomerAgent.cs`, `Agents/GroomerAgentFactory.cs`, `Core/IssueGroomerRunStore.cs` (schema v8), `Orchestrator/ScheduledGroomer.cs` (5-min tick) | `POST /api/specs/{id}/groom` (manual) | Stories + tasks via `IssueStore.CreateAsync` (parent chain `task → story → spec`) |
| Sprint assembly | `Orchestrator/Sprint/SprintAssembler.cs` (5-min tick), `Orchestrator/Sprint/SprintProposeService.cs` | Autonomous (5-min tick) | `SprintStore.SetActiveAsync` |
| Engineering dispatch | `Orchestrator/OrchestratorAgent.cs`, `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` | Dispatch loop | Worktree → agent → commit → push → PR → watch |
| PR watch + review + rework + merge | `Reviewer/PRWatcher.cs`, `Reviewer/ReviewerDispatcher.cs` | 15-min sequential sweep (review-then-poll per watch) | Merge on green CI + approval; rework round on red CI / changes-requested / conflict |

**Spec state machine** (`Core/SpecStore.cs::SpecStatus`):
`Draft` → `ReadyForDesign` → `Designed` → `AssetReady` →
`ReadyForGroom` → `Grooming` → `Groomed` → `Shipped`. Transitions
are validated by `Core/SpecStore.cs::SpecStatusTransitions`. The
Groomer gate widens to `Designed | AssetReady | Approved | Groomed`
— both visual specs (with Meshy art) and non-visual specs
(operator-approved) flow into the Groomer.

**Stage gates** (`Core/StageGates.cs`). Optional operator review
gates at the pipeline's major automatic transitions: `design`,
`groom`, `sprint` (assembly only), `merge`. A held gate pauses
that stage (its scheduler skips the tick; a held merge leaves the
watch live and unmerged) until the operator releases it via the
Sprints page gate strip or `GET/POST /api/gates*`. State lives in
the project's `MemoryStore` as `gate/<stage>` = `"hold" | "open"`
(absent = open) — no schema change, inspectable with the normal
memory tooling.

**Memory extraction** (`Orchestrator/MemoryExtractor.cs`,
`Orchestrator/MemoryExtractionStore.cs`). Runs after a successful
commit; asks the LLM to extract design decisions (one small call),
writes via `MemoryStore.AddAsync`, and records the row in the
`memory_extraction` table (P5 design in
`docs/p5-shared-context.md`). New tasks see the decisions in the
`## Project memory` block.

**Context handoff** (`Core/ContextHandoffStore.cs`). Records
`(task_id, from_role, to_role, artifact_id, consumed, created_at)`
when `read_artifact` is called. Cheap audit trail for the closed
loop.

**Plan gate** (see § 3.4 Stage 3). Hard-enforced at the tool
layer; surfaces as `submit_plan` tool to the engineering agent and
`ShellMutationClassifier` refuses mutating bash until approval.

**Cost + rate-limit governance** (`Core/CostTracker.cs`,
`Core/ModelRateLimitTracker.cs`, `Agents/RateLimitAwareChatClient.cs`).
ONE rate-limit tracker is shared process-wide; a per-provider
semaphore (`llm.maxConcurrentRequests`, default 2) caps simultaneous
round-trips across all subsystems. `Headroom` proxy is an opt-in
layer (`HeadroomOptions.Enabled`); see
`docs/headroom.md` for the operator guide.

### 4.2 Review loop

The review loop is automatic and STATE-DRIVEN (operator rule
2026-07-29 — `pr-watch` issue rows are retired). The sequence:

1. `RunAgentExecutor` advances to `pr_opened`; `OnPrOpened` fires
   (subscriber hooks in `EngineeringDispatchWorkflow`).
2. `PRWatcher.PollWatchedTaskAsync` (`Reviewer/PRWatcher.cs:95`)
   reads CI from **check runs** (not legacy combined statuses — they
   don't see GitHub Actions) and merges when: CI green AND
   (formal Approved review OR reviewer-agent `Approve` at the
   current head).
3. **Review** (`Reviewer/ReviewerDispatcher.cs::ReviewOnceAsync`):
   fetches the PR diff, runs the Reviewer role, records the verdict
   in watch metadata (`reviewSha`/`reviewVerdict`/`reviewNotes`/
   `reviewRound` — the machine record), posts a GitHub comment
   (the audit). Per-head-SHA dedupe; `Error` verdicts retry next
   sweep. Formal review submission is opportunistic (solo-identity
   422 tolerated — the local verdict is authoritative). The
   reviewer fetches the live `origin/<branch>` SHA via
   `GitHubService.GetBranchHeadShaAsync` and prefers the live SHA
   over `pr.Head.Sha` so stale PR objects don't drive false
   REQUEST_CHANGES verdicts.
4. **Rework loop**: CI failure or changes-requested → task back to
   `Pending` with `reworkAttempts`/`reworkContext` metadata (the
   agent prompt surfaces it as "## Rework required"), watch stays
   live, worktree kept, `reworkInFlightSha` prevents re-triggering
   on the same head. Circuit breaker at `PRWatcher.MaxReworkAttempts`
   (3) → terminal `Failed` (CI) / `Blocked` (review) for the
   operator. Reviewer-error also breaks to `Blocked` (manual review). The reworked task pushes to the SAME branch —
   `CommitPushPrExecutor` reuses the existing PR; `EnqueueWatchExecutor`
   dedupes watches per PR.
5. **Non-blocking findings are NOT requested-changes.** The
   reviewer approves and files them via `file_followup` (they
   re-enter through grooming like everything else).
6. **Merge:** `GitHubService.MergePullRequestAsync` (squash merge
   default), branch delete + worktree removal
   (`GitWorktreeService.RemoveAsync`).

### 4.3 Recovery (P4)

Two stages; Stage A is on by default.

**Stage A — In-process `StartupRecovery`**
(`Orchestrator/StartupRecovery.cs`). Runs at every orchestrator
startup. Classifies every `InProgress + assignee=forge` issue by its
`dispatch_checkpoint` (`Core/DispatchCheckpoint.cs`):

| Value (DB) | Enum | Side-effect already done | Replay from |
|---|---|---|---|
| `claimed` | `Claimed` | status=InProgress, assignee=forge | Worktree acquisition |
| `worktree_acquired` | `WorktreeAcquired` | + `worktreePath` + `branch` set in metadata | Agent run |
| `agent_completed` | `AgentCompleted` | + `modelResponse` set in metadata | Commit |
| `commit_done` | `CommitDone` | + `branchSha` updated | Push |
| `push_done` | `PushDone` | (no extra metadata) | PR open |
| `pr_opened` | `PrOpened` | + `prNumber` set | (no replay — `PRWatcher` path takes over) |

The convention in every executor is "advance the checkpoint
**before** the side-effect", so on a crash the recoverer knows
whether the side-effect happened. Replays are idempotent:
`git push` is a no-op if the remote is up-to-date; re-opening a PR
fails gracefully if one already exists; the recoverer reuses the
existing PR when `prNumber` is set.

The audit table is `recovery_report`
(`Core/RecoveryReportStore.cs`); rows carry
`(ts, spec_id?, issues_scanned, issues_replayed, issues_failed,
duration_ms, actions_json)`. `actions_json` is an array of
`RecoveryActionRecord { IssueId, BeforeCheckpoint, AfterCheckpoint,
Action, Error }` where `Action ∈ { "replay", "failed",
"left_alone", "already_recovered" }`.

CLI/API surface:

| Flag / endpoint | Behavior |
|---|---|
| `--recover` | Dry-run: see what `StartupRecovery` would do. No side-effects. |
| `--recover-and-start` | Replay unfinished side-effects, then start dispatch. |
| `POST /api/recovery/run` | Same as `--recover-and-start` (HTTP). |
| `POST /api/recovery/dry-run` | Same as `--recover` (HTTP). |
| `GET /api/recovery/reports` | List of past reports (most recent first). |

**Hard-fail rule:** after
`StartupRecoveryOptions.MaxAttempts` (default 3) recoveries for a
single issue, the issue is hard-failed. The operator must intervene
via the dashboard's Recovery tab (inspect the audit row, fix the
underlying cause, force a fresh sweep).

**What Stage A does NOT cover:**

- Designer / Artist / Groomer schedulers — they use fresh MAF
  agents per run, not the engineering workflow. Stage A does not
  replay their state.
- Long-running open `pr-watch` issues — those are owned by
  `PRWatcher` polling, not the engineering workflow. (Post 2026-07-29
  rule change, the task itself is the watch; see § 4.2.)

**Stage B — Durable Task Scheduler** (opt-in via
`Orchestrator:Execution=Durable`; `Orchestrator/DurableDispatcher.cs`).

When Stage B is on, the orchestrator's workflow state persists
across crashes in the DTS sidecar. `StartupRecovery` becomes
largely redundant for engineering-dispatch work (it still runs, but
should find nothing to do). Sidecar: DTS emulator image
`mcr.microsoft.com/dts/dts-emulator:latest`, ports 8080/8082. Both
`docker compose` and `podman-compose` work
(`deploy/docker-compose.yml`). For prod, swap to the hosted Azure
Durable Task Scheduler — same gRPC contract, no code changes.

| Concern | Stage A | Stage B |
|---|---|---|
| Operational cost | None — pure in-process | Sidecar container to bring up + monitor |
| Crash window | Anything between two checkpoint writes can be lost on crash (small — milliseconds) | Workflow state persists across crashes |
| Test surface | `StartupRecovery`-specific unit tests + 3 kill-restart tests in `docs/p4-restart-safety.md` | `Microsoft.Agents.AI.DurableTask` integration tests |
| Migration cost | Zero — it's on by default | Requires `Orchestrator:Execution=Durable` switch flip + sidecar |
| When to recommend | Operator runs the orchestrator on a single host, accepts restart-time replay | Multi-host / HA / strict "no replay ever" |

The convention "advance the checkpoint BEFORE the side-effect" is
the same on both stages; Stage B's durable workflow checkpoints are
synonymous in spirit.

### 4.4 Dashboard

`Dashboard/DashboardHost.cs` is the composition root. All
endpoints are minimal-API (`Dashboard/*Endpoints.cs`), grouped by
concern and registered from `DashboardHost.cs`. The page itself is
Blazor Server over a `Microsoft.NET.Sdk.Web`-library sibling
(`Forge.UI/Forge.UI.csproj`); `Forge.Core/Forge.Core.csproj`
references it and pulls its `wwwroot` as static web assets.

Pages (current):

| Route | Surface | Backing endpoint family |
|---|---|---|
| `/` | AppShell — sprint pill, search, heartbeat, live feed | `GET /api/sprints/active`, `GET /api/search`, `GET /api/health/heartbeat`, `GET /api/events` (SSE) |
| `/tasks` | In-progress tasks + Retry/Recover buttons | `GET /api/tasks/in-progress` + `TaskEndpoints` |
| `/backlog` | All issues with status filters | `GET /api/state` |
| `/specs` | Spec grid + filter chips + action-state matrix | `GET /api/specs`, `GET /api/specs/{id}/actions` |
| `/designs` | Design Kanban (ready/needs-revision/designed) | `GET /api/designs?projectId=...&status=...` |
| `/art` | Art gallery + Meshy task timeline | `GET /api/art-output?projectId=...` |
| `/intake` | 3-pane (queue · drafts · global handoff) | `GET /api/intake/...` |
| `/sprints` | Sprint cards (active / committed / completed) | `GET /api/sprints/propose-next`, `/scoring-audit` |
| `/agents` | Registered agents + heartbeat + slot meter | `GET /api/state`, `GET /api/agents` |
| `/skills` | Loaded skills + agent bindings | `GET /api/state` |
| `/vision` | `docs/MASTER_DESIGN.md` rendered + Refresh button | `GET /api/vision`, `POST /api/vision/refresh`, `PUT /api/vision` |
| `/projects`, `/projects/{id}/overview` | Registered projects + per-project counters + role caps | `GET /api/projects/` |
| `/board` | Cross-project kanban feed | `GET /api/board` |
| `/ops/...` | Recovery, memory extractions, headroom, cost | various `/api/ops/...` + `/api/recovery/...` + `/api/memory/extractions` + `/api/cost/...` |
| `/search?q=` | Built-in search | `GET /api/search?q=` |
| `/flow` + `/flow?issue={id}` + `/flow?mode=edit` | Pipeline DAG + per-issue journey + workflow editor | `FlowEndpoints`, `WorkflowEndpoints` |

`Forge.UI/wwwroot/app.css` holds the design-system classes
(`.card`, `.pill--*`, `.data-grid`, `.slot-card`, `.banner--*`,
`.role-*`). Shared concepts render through shared components
(`<RoleSlotMeter>` everywhere — operator rule 2026-07-24). New
shared visuals get an `app.css` class, not a page-local inline style.

The Flow page (`/flow`) is the live pipeline DAG: planning lane
(specs/ad-hoc) vs implementation lane (tasks), per-node counts, and
a per-issue journey view derived from the `issue_event` timeline.
**Edit mode** (`/flow?mode=edit`) is the workflow control surface:
the pipeline is a `WorkflowDefinition`
(`Core/Workflow/WorkflowDefinition.cs`, built-in default = the
previously hardcoded DAG) edited as draft → validated publish →
memory-key override (`workflow/live`, snapshots under
`workflow/versions/`). Wiring & policy edits only — the transition
table stays code-owned; gates/policies/step-toggles/branch options
resolve per evaluation, no restart.

The dashboard SSE stream is `GET /api/events`
(`Dashboard/DashboardHost.cs:535`); events publish through
`IDashboardEventBus` (impl: `Dashboard/InMemoryDashboardEventBus.cs`)
and replay the last ~1024 on connect.

## 5. Cross-cutting constraints (the "non-negotiables" you will hit)

These come from `AGENTS.md`, `CONTRIBUTING.md`, and operator rules
documented in code comments. They are non-negotiable in code review
and in any agent's first stop on a confused branch.

1. **TreatWarningsAsErrors=true** on the main project
   (`Forge.Core/Forge.Core.csproj`). New code must compile cleanly.
2. **No Moq / NSubstitute.** Hand-roll typed fakes
   (`CONTRIBUTING.md § "Things to avoid"`).
3. **Engineer agents must NOT open a PR.** The orchestrator opens
   it via `GitHubService.CreatePullRequestAsync`. The agent pushes
   the branch and stops. Asserted in `BuildPrompt`
   (`Orchestrator/OrchestratorAgent.cs:314`).
4. **Don't bypass `IssueStore`** to write the queue. All task
   state goes through `CreateAsync` / `ClaimAsync` /
   `TransitionAsync` / `UpdateMetadata`.
5. **The JSONL mirror is a viewer artifact, not source of truth.**
   `IssueStore` wins on disagreement
   (`Core/IssuesJsonlMirror.cs`, regenerated every 5 s).
6. **Schema changes**: bump `CurrentSchemaVersion` in
   `Core/IssueStore.cs`, update BOTH DDL paths (SQLite migration
   chain in `InitializeSchemaSqlite`, SQL Server fresh-create in
   `InitializeSchemaSqlServer`), run `--check` after
   (`AGENTS.md § "Schema changes"`; `CONTRIBUTING.md § "Schema
   migrations"` is authoritative).
7. **Cross-process safety:** only one orchestrator per state
   directory. SQLite WAL allows concurrent readers but a second
   writer waits up to the busy-timeout
   (`AGENTS.md § "Cross-process safety"`).
8. **Per-role territory** (`Agents/RoleAgentRegistry.cs`):
   `CoreDev` → Forge backend modules + `tests/` + `docs/` +
   `.kilo/` + `agents/` + `deploy/` + `scripts/` + `.github/` +
   `tools/` + `Reviewer/` + `DeploymentPipeline/`. `ClientDev` →
   `Forge.UI/` + `tests/`. `QA`, `Reviewer`: read-only. Each
   role's `TerritoryPrefixes` is enforced by `PlanTerritoryGate`
   (`Agents/Gates/PlanTerritoryGate.cs`). A per-project
   `roles_json` override wins wholesale.
9. **Don't auto-clear `Failed` issues.** `ReadyAsync` treats
   `Failed` blockers as intentionally open.
10. **No manual out-of-loop fixes.** Don't hand-merge,
    hand-push, or hand-patch around the loop. Either fix the
    system or surface it.
11. **Skills are per-project and dual-owned** (schema v24). Repo
    is source of truth for repo-owned (`source='repo'`) skills;
    dashboard edits win for UI-owned (`source='forge'`) skills.
    Role prompts also resolve per project now: `MafAgentRunner`
    loads `<projectRoot>/agents/<role>.md` for the run's project
    when the project ships one, falling back to the built-in root.
12. **The Agents page is the agent control surface.** Role
    catalog is canonical (`RoleAgentRegistry.All()` +
    `RoleAgentRegistry.Pipeline` + `AllSlotRoles`). Model overrides
    are live and DB-backed (`Agents/RoleModelOverrides.cs`, memory
    keys `llm/roleModel/<AgentType>`; `PUT/DELETE
    /api/agents/roles/{name}/model`).
13. **Engineering concurrency is per-role, not global**
    (`Orchestrator/Slots/SlotTable.cs`). `SlotTable` holds a
    semaphore per (project, role); the dispatch loop acquires a
    role slot per ready sprint task (zero-timeout — a full pool
    skips only that role's tasks, other roles keep claiming).
    Caps come from the project's `roles_json` falling back to
    `DefaultProjectRoles` (coredev/clientdev/reviewer=2,
    others=1).
14. **Vision content lives at `docs/MASTER_DESIGN.md`** (this
    file). The dashboard's Vision tab renders it; the
    `vision/master` memory key carries it into every agent prompt.
    `PUT /api/vision` writes the file and refreshes the key.

## 6. North-star direction

The repository already documents what comes next in
`docs/intake-to-sprint-workflow.md`, `docs/p5-shared-context.md`,
and `docs/p6-ui-mockups.md`. The items below are the principles
future work should align to. None are invented roadmap items —
each is grounded in something already in the code or docs.

- **Build Forge with Forge.** Register a `forge` project entry
  (id `forge`, repoUrl `https://github.com/Xyrces/Forge.git`) and
  let the orchestrator dispatch engineering work against its own
  repo. The infrastructure for this is in place
  (`appsettings.example.json` shows the format; the per-project
  bundle + secret + dispatch machinery is production-ready). The
  first-generation agent prompt that drove this document was itself
  a step on that path.
- **Tighten the closed loop on context, not just state.** The P5
  design (`docs/p5-shared-context.md`) outlines a native
  shared-context layer: spec body becomes an index, not a payload;
  `read_artifact` AIFunction lets the next agent pull bodies on
  demand; `context_handoff` lineage + auto-extracted memories on
  commit. Headroom (`docs/headroom.md`) is the operator's opt-in
  compression proxy; revisit it if the spec-body split leaves
  long-context cache misses.
- **Reactive plan-gate and stage-gate overrides.** Both gates
  resolve DB override → config → built-in defaults today
  (`Agents/Gates/RunGatePipeline.cs::ResolveWithSourceAsync`,
  `Core/StageGates.cs`). The next step is a dashboard editor for
  the per-checkpoint gate list (the read-only catalog already
  ships at `GET /api/gates/preImplementation`).
- **Workflow edit-mode (`/flow?mode=edit`).** The DAG is now an
  editable `WorkflowDefinition` (validated publish → `workflow/
  live` memory key, snapshots under `workflow/versions/`). Wiring
  & policy edits only — the transition table stays code-owned.
  This is the operator's knob for tuning the pipeline without a
  restart.
- **Webhook-driven PR signals** (currently 30 s polling in
  `PRWatcher`). Deferred per `docs/vision-status.md`: the polling
  cadence is fine for Stage B's needs; revisit if multi-host HA
  needs sub-second merge propagation.
- **Web search + online docs** for engineering roles — the `bash`
  AIFunction's `webfetch` tool is on the allowlist but ungated;
  future work could surface it as a first-class tool with rate
  limits.
- **Multi-project dispatch (full)**. v1 made the dashboard
  multi-project-aware and the per-project bundle factory is wired;
  hot-add of dispatch targets for non-primary projects at runtime
  is the next leap (currently the bundle cache + dispatch loop
  assume the primary project for per-claim multi-project
  iteration).
- **Azure SQL cutover** (`docs/azure-sql-cutover.md`). The
  rehearsal ran end-to-end on 2026-07-27 (per `README.md § "CLI"`).
  Production cutover is a config switch + the `--init-azure-sql`
  one-shot.
- **Bring-the-pipeline-to-the-model.** The current production
  path is the sequential code in
  `OrchestratorAgent.DispatchSingleTaskAsync` (it claims
  up-front and hands off to the dispatcher). The workflow
  executors ARE the production path; the dormant MAF Workflows
  graph (`Orchestrator/Workflow/EngineeringDispatchWorkflow.cs`)
  has full `AlreadyClaimed` / `NoDiff` short-circuit parity and
  is exercised by `tests/Forge.Tests/Integration/EngineeringDispatchWorkflowTests.cs`.
  Convergence here is the natural next deliverable.

## 7. Reading order for newcomers

This is the canonical order, lifted from
`CONTRIBUTING.md § "Reading order if you're new"` and refined:

1. `docs/system-flow.md` — what runs when a task is dispatched.
2. `docs/MASTER_DESIGN.md` (this file) — the orientation.
3. `docs/vision-status.md` — which phase of
   `docs/agent-framework-design.md` is live, with commit links.
4. `docs/operator-cookbook.md` — how an operator uses the system
   (common scenarios, the dashboard's surface, CLI recipes).
5. `Program.cs` — the CLI entry point and composition root.
6. `Core/IssueStore.cs` — the heart of the system; everything else hangs off this.
7. `Orchestrator/OrchestratorAgent.cs::DispatchSingleTaskAsync` —
   the production dispatch loop.
8. `Agents/MafAgentRunner.cs` — how the agent runs.
9. `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` — the
   MAF-Workflows version (dormant infrastructure; production
   delegates to it via `IWorkflowDispatcher`).
10. `Dashboard/DashboardHost.cs` — how the HTTP surface composes.
11. `tests/Forge.Tests/IssueStoreTests.cs` — the best entry point
    into the test suite.

For the dependency-graph and JSONL mirror design, see
`docs/embedded-issues.md`. For the multi-project + slot dispatcher,
see `docs/multi-project.md`. For Linux deployment, see
`docs/linux-deployment.md`. For the system's narrative arc, see
`docs/agent-framework-design.md` (now historical; `docs/vision-status.md`
is the current source of truth for what's live).

## 8. Where to file a follow-up

Operator rule (`AGENTS.md § "Where to add follow-up skills / rules"`):

- Drop a recurring-need skill at
  `.kilo/skills/<name>/SKILL.md` with `name` and `description`
  frontmatter.
- Slash commands at `.kilo/command/<name>.md`.
- Kilo subagent definitions at `.kilo/agent/<name>.md` (singular).

The skills that ship today:

- `.kilo/skills/forge-architecture/SKILL.md` — navigation map.
- `.kilo/skills/forge-task-lifecycle/SKILL.md` — the engineering
  dispatch pipeline + `PRWatcher`.
- `.kilo/skills/forge-recovery/SKILL.md` — P4 Stage A in-process
  recovery vs P4 Stage B Durable Task Scheduler sidecar.
- `.kilo/skills/forge-secrets/SKILL.md` — the per-project secrets
  system.

When stuck, run `dotnet run --project Forge -- --check`. If that
passes, the bug is in the dispatch path. If it fails, the failure
message names the subsystem to investigate. From there, the
dashboard's Flow page (`/flow`) is the first stop for "where is my
work stuck?", followed by the JSONL mirror (`tail -f .portHorizon/
state/issues.jsonl`), the per-run diagnostic
(`<dataRoot>/logs/agent.log` set by `Program.cs` →
`MafAgentRunner.DiagnosticLogPath`), and the `Events` SSE stream.

## 9. Cross-references

| Topic | Doc |
|---|---|
| Engineering dispatch pipeline | `docs/system-flow.md § "Data flow: dispatch one task"`, `docs/p4-restart-safety.md`, `docs/operator-cookbook.md § "Watch a PR through the review loop"` |
| Intake → Sprint workflow (future-facing) | `docs/intake-to-sprint-workflow.md` |
| Recovery (P4 Stage A + Stage B) | `docs/p4-restart-safety.md`, `Orchestrator/StartupRecovery.cs` |
| Multi-project + slot dispatcher | `docs/multi-project.md`, `Orchestrator/Slots/SlotTable.cs` |
| Per-project secrets | `.kilo/skills/forge-secrets/SKILL.md`, `Core/SecretStore.cs`, `Dashboard/SecretsEndpoints.cs` |
| Vision (this file) | `Dashboard/VisionStore.cs`, `Dashboard/VisionEndpoints.cs`, `tests/Forge.Tests/VisionEndpointTests.cs` |
| Cost + rate limits | `docs/headroom.md`, `Core/CostTracker.cs`, `Core/ModelRateLimitTracker.cs`, `Agents/RateLimitAwareChatClient.cs` |
| Headroom compression proxy | `docs/headroom.md` |
| Linux deployment | `docs/linux-deployment.md` |
| Self-deploy from merged commit | `docs/deployment-pipeline.md` |
| E2E smoke harness | `docs/e2e-harness.md` |
| MAF migration (historical) | `docs/agent-framework-design.md` |
| Phase status (current source of truth) | `docs/vision-status.md` |
| Plan gate catalog + UI | `Dashboard/RunGateCatalogEndpoints.cs`, `Dashboard/GateVerdictEndpoints.cs` |
| Spec pipeline (Designer + Artist + Groomer) | `docs/operator-cookbook.md § "Design a spec"` + `§ "Run the Artist"`, `Orchestrator/DesignerScheduler.cs`, `Orchestrator/ArtistScheduler.cs` |
| Native shared context (P5) | `docs/p5-shared-context.md` |
| Dashboard UI design (P6) | `docs/p6-ui-mockups.md` |
