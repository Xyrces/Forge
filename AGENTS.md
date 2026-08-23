# AGENTS.md — Forge

Rulebook for any agent (kilo or otherwise) working on this repository. Loaded automatically by Kilo Code; treat as authoritative alongside `CONTRIBUTING.md`.

For a narrative overview see `README.md` and `docs/system-flow.md`. This file is the rule layer, not the narrative layer — keep it short and pointed.

## Critical `.kilo` directory distinction

There are two completely different `.kilo` directory trees in this repo. Confusing them breaks both:

| Path | Owner / consumer | Purpose |
|---|---|---|
| `agents/` (already exists with `coredev.md`, `clientdev.md`, `qa.md`, `reviewer.md`) | **Microsoft Agent Framework (MAF)** runtime — loaded by `Agents/MafAgentRunner.cs::LoadRoleInstructions` via the YAML `description:` frontmatter | System prompts for the engineering role agents. Driven by `Core.AgentType` and `Agents/RoleAgentRegistry.cs`. |
| `.kilo/agent/` (singular) | **Kilo Code subagents** — invoked via the Task tool | Subagent definitions Kilo can delegate work to. |
| `AGENTS.md`, `.kilo/skills/...`, `.kilo/command/...` | **Kilo Code** itself — auto-loaded rules and slash commands for the operator/agent session | Conventions + navigation aids. |

Do **not** edit a role's system prompt thinking you are editing a kilo agent. They are separate worlds.

**Role prompt resolution** (`Agents/RolePromptRoot.cs`): `<project-root>/agents/<role>.md` wins; otherwise the orchestrator falls back to the built-in `agents/*.md` copied next to the app at publish time (csproj `Content` include). A project whose repo has no `agents/` dir still gets the real role instructions; committing an `agents/` dir into that repo overrides per-project.

## Module boundaries (non-negotiable)

Lifted from `CONTRIBUTING.md` § "Boundaries":

- `Core/` has no I/O beyond the state database (SQLite or Azure SQL, via the `Core/Db` provider seam). No HTTP, no GitHub, no LLM. Stores take a connection factory (or a SQLite path) via the constructor; they don't read env vars or config. **Event seam (2026-08-08):** `Core/Messaging/` holds the pure-record event contracts + `IEventPublisher` (`NullEventPublisher` default, no Talaria reference in Core). Stores publish AFTER a mutation commits — publication cannot be skipped by a forgetful caller, and publish failures are swallowed by the publisher (a hint never breaks a DB mutation).
- `Agents/` depends on `Core/`, `Configuration/`, `Dashboard/`. Publishes `DashboardEvent`. Does not read `appsettings.json` directly.
- `Orchestrator/` depends on `Agents/` + `Core/`. Glues stores + agent + git + GitHub.
- `Messaging/` depends on `Core/` + the Talaria packages (`Talaria.Core`, `Talaria.Transports.InMemory` — GitHub Packages, see `nuget.config`). Hosts `TalariaEventPublisher`, the `EventConsumer<T>` BackgroundService base, `SweepTickPublisher` (15m backstop), and the transport factory (`messaging.transport=inmemory` default; `servicebus` reserved).
- `Dashboard/` depends on `Core/` + `Configuration/`. Reads stores; publishes/subscribes events via `IDashboardEventBus`.
- `Reviewer/` is empty today — reserved for future designer/artist roles.

If a class needs to read `IOptions<X>` AND write `IssueStore` AND make HTTP calls, that is a code smell. Split it across Core / Agents / Orchestrator.

## Conventions (from `CONTRIBUTING.md`)

- `TreatWarningsAsErrors=true` on the main project. Clean build required. Test project allows warnings.
- `LangVersion=14`, `<Nullable>enable</Nullable>`. Use `string?` for nullable params.
- Tests: xUnit + FluentAssertions-light. **No Moq, no NSubstitute.** Hand-roll typed fakes.
- Use `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance` for no-op loggers in tests.
- AIFunction optional params need C# default values (`string? param = null`, not `string? param`). The MAF binder throws `ArgumentException` otherwise.
- Never swallow exceptions in production paths. Log them or return early. Never `try { ... } catch (Exception) { }`.
- Never use `Task.Run` to "fix" an async signature. If a method is async, await it. If it isn't, don't fake it.
- Don't add `--dashboard-only`-style escape-hatch CLI flags without operator sign-off. They tend to outlast their purpose.
- **Planning-lane store routing (operator rule, 2026-07-29, after the live misrouting incident).** Every issue row a pipeline stage creates belongs to the store OWNED by the work's project: the groomer writes stories/tasks to `spec.ProjectId`'s store (`GroomerAgentFactory.Create(projectId:)` + `issueStoreLookup`), intake writes epics to the session project's store, and the ad-hoc groomer sweeps every registered project's queue (`ScheduledGroomer` + `ProjectContextFactory`). The primary store is NOT a global queue — a row for project B physically in project A's store enters A's sprint lane and gets dispatched against A's repo (porthorizon stories produced bogus Forge PRs #66/#67). `SprintAssembler.DropCrossProjectGroupsAsync` is the defense-in-depth guard: it refuses to assemble tasks whose spec is owned by another project and logs an error. **Schema-per-project IS the isolation boundary (operator rule, 2026-08-09, after the epic-2 collision incident):** cross-project reads exist for exactly two situations — the pipeline schedulers that advance every project's lane (groomer/designer/artist sweeps) and the unified `/now` admin view — and must be EXPLICIT (`ProjectRoutingSpecStore.ListAcrossProjectsAsync` / `ListForPipelineSweepAsync`; unscoped `ListAsync(null)` on the routing store throws). Issue ids are per-store sequences (every project has an epic-2/task-5): NEVER match fan-out rows by issue id or `parent_issue_id` across stores — spec ids (random hex) are the only safe cross-store keys.
- **Skills are per-project and dual-owned (schema v24, operator rule 2026-07-28).** `skill.project_id` NULL = global (every project's runs see it); a project-scoped row injects only into runs for that project (the dispatch context's `projectId` drives `ISkillSource.LoadForRoleAsync(role, projectId)`; same-name global + project copies resolve in favor of the project copy). `skill.source`: `forge` = UI-owned (dashboard edits win; seeding never overwrites); `repo` = imported at startup from the project's `.kilo/skills/<name>/SKILL.md` — the **repo is the source of truth**: SKILL.md edits propagate on boot, removed files delete rows, and the dashboard returns 409 on edit/delete (`RepoOwnedSkillException`). Role prompts also resolve per project now: `MafAgentRunner` loads `<projectRoot>/agents/<role>.md` for the run's project when the project ships one, falling back to the built-in root. The groomer reads `vision/<projectId>` (falling back to `vision/master`) and resolves its repo-shape grounding per project via `ProjectContextFactory.KnownProjects`.
- **UI consistency is enforced** (operator rule, 2026-07-24): shared concepts render through shared components (a slot is `<RoleSlotMeter>` everywhere); no inline `style=` for shared visuals — design-system classes in `Forge.UI/wwwroot/app.css` (`.card`, `.pill--*`, `.data-grid`, `.slot-card`, `.banner--*`, `.role-*`); new shared visuals get an `app.css` class, not a page-local style; cross-link related surfaces, never edit the same setting in two places (caps = project drill-down, models/prompts = `/agents`).
- **Branch protection on the default branch (operator rule, 2026-08-07).** ALL work on this repo — including interactive Kilo/agent sessions — must happen on a feature branch and land via a pull request. Never commit or push directly to the protected default branch; cut a branch first, then open a PR.

## Agent-specific rules (the ones an LLM agent most often gets wrong)

- **Internal coordination is message-driven (2026-08-08, Talaria).** Stores publish hint events after mutations; `Orchestrator/Consumers/` (one consumer PER TOPIC — the in-memory transport is competing-consumer, two consumers on one topic steal each other's messages) fan out to `WakeupSignal`s that kick the dispatch loop and the groomer/designer/artist/assembler loops; the watch sweep runs on `SweepTick(watch)` with `PrOpened`/`ReviewVerdictRecorded`/MergeReady fast paths. `TaskEnqueued` kicks dispatch + assembler + GROOMER (a freshly created parentless task — e.g. a materialized sprint follow-up — is born ungroomed; this is also the groomer's drain mechanism: each approval/close publishes its own re-kick, so the follow-up backlog drains continuously instead of 3-per-15m). Messages are HINTS, not truth: every handler re-reads DB/GitHub state and is idempotent. The 15-minute backstops (SweepTickPublisher + per-loop backstop waits) re-derive everything if hints are lost; GitHub remains the only polled external system. LLM/GitHub cooldown timers stay internal — never bus messages.
- **Failure triage (phases 1-2, schema v35).** `IssueStore.TransitionAsync` publishes `TaskFailureSignal` on failure-status boundary crossings (incl. metadata-stamped clearances — the `clearanceAction` + per-gesture `clearanceActionAt` nonce, phase-1 C4 edge); `FailureTriageConsumer` writes the `failure_triage` ledger (open → action → outcome, idempotent via store guards). Phase 2: a per-project opt-in flag (`$triage` in `project.roles_json`, `PUT /api/projects/{id}/triage`, the /triage banner toggle) gates a `TriageRequested` kick to `TriageConsumer` (own topic), which re-derives flag + ledger + the deterministic `TriageGuardrails` (≤2 triage actions/task/day, same-signature requeue twice without success = park) before running the `triage` role agent (`Agents/TriageAgent.cs`, prompt `agents/triage.md`, own AgentType/editable model). The agent's ONLY actions are `TriageTools`: requeue_with_guidance (spends a strike round — counters NOT reset), park_for_operator, flag_bug_suspect (ledger flag only, never creates issues). Every action audits with actor=triage on the ledger + `triageAction`/`triageNote` task metadata (TaskDetail strip).
- **Composition root is DI.** `Orchestrator/Composition/ForgeComposition.cs` builds the whole runtime graph in a `ServiceCollection` (stores as singletons, schedulers/consumers started by `Program.RunOrchestratorAsync`); `DashboardHost` is factory-registered so its separate WebApplication container gets the SAME transport + `IEventPublisher` instances. New runtime services register there — no hand-wired ctor chains in Program.cs.

- **Engineer agents must not open a PR.** The orchestrator's `OrchestratorAgent.DispatchSingleTaskAsync` opens the PR via `GitHubService.CreatePullRequestAsync`. The agent pushes the branch and stops. This is asserted in `BuildPrompt` (`OrchestratorAgent.cs:314`).
- **Task lifecycle is a state machine** (`Core/TaskState.cs` + `Core/TaskStateMachine.cs`). Watcher/dispatch/reviewer report OBSERVED events; the machine validates against the transition table and records `state`/`stateEnteredAt`/`reworkForSha`/`parkedForSha` on the task. Readers (guards, projector, dashboard) consume the machine record — never invent ad-hoc state flags (the reworkInFlightSha/parkedOnMainCiSha era ended 2026-07-26). `state.writeAuthority=true`: illegal transitions log errors + `stateViolation` metadata (never thrown). New cross-state behavior = new table entry, not a new flag.
- **Sprint flow: ALL engineering work happens inside a sprint.** The `SprintAssembler` (5-min tick) completes the Active sprint when its tasks are terminal and assembles + activates the next from eligible Pending tasks (groomed-spec groups FIFO via the task→story→spec parent chain; groomed ad-hoc tasks last). `OrchestratorAgent` never dispatches a dev task that isn't linked to the Active sprint (the watch sweep is exempt — it's lifecycle, not sprint work, and discovers watched tasks by `prNumber` metadata). There is deliberately no UI button to create sprints. Agent runs inside a sprint get the sprint goal + sibling roster in the prompt and shared `sprint/{id}/` memory keys.
- **A sprint is a themed, coherent, independently deployable unit — there is exactly ONE kind of sprint (operator rule, 2026-08-08, superseding the 2026-07-27 solo-sprint model).** Sprint assembly packs ALL eligible work sharing a theme into one sprint: spec-chained tasks theme under their groomed spec, and follow-up work themes under its `followUpOf` ROOT ancestor (`followup:<rootId>` — chain-convergent follow-ups are definitionally the same work; capped at 10 tasks per sprint, remainder packs the next). Follow-ups NEVER get solo sprints. Only truly rootless ad-hoc tasks (operator-enqueued, no chain) still assemble one-per-sprint — they are the genuinely unrelated work. Theme choice is priority-first (the theme's highest-priority member), then oldest; the groomer sets each follow-up's priority RELATIVE to the whole open-work backlog at approve time (`approve_task(note, priority)`), so assembly always builds the most important theme. Mid-sprint **injection** is unchanged: a `blocks` dep edge into a sprint member, operator P1 / `blocker=true`, or operator requeue (`requeuedFromFailedAt`). Intake stays the entry point for NEW work (epics); backlog tasks flow through grooming only.
- **No task enters a sprint without technical grooming (operator rule, 2026-07-23).** Ad-hoc tasks (operator-enqueued, agent-filed follow-ups) are NOT sprint-eligible until the `ScheduledGroomer` ad-hoc pass marks them `groomed=true` (or closes them). The groomer verifies against the project vision (`vision/master` memory key) and plans against current state (open-work digest + repo shape) — this grounding is also in the spec-groom prompt. Agent-filed follow-ups (`file_followup` tool on CoreDev/ClientDev/QA/Reviewer) are deliberately parentless so they can't inherit a groomed chain's eligibility; `followUpOf` metadata is the audit trail.
- **Operator stage gates** (`Core/StageGates.cs`): optional hold/release at the four automatic transitions — `design`, `groom`, `sprint` (assembly only; completing a finished sprint is bookkeeping), `merge` (hold leaves the watch live; external merges still detected). Driven via `GET/POST /api/gates*` or the Sprints page gate strip. Held stage = its scheduler skips the tick and logs. v1 is backed by the primary project's memory store (`gate/<stage>` keys).
- **Review loop is automatic and watches are STATE-DRIVEN (operator rule 2026-07-29 — `pr-watch` issue rows are retired).** The task IS the watch: `prNumber`/`branch`/`worktreePath` metadata + the lifecycle states on the task row are everything the watcher needs, so the sweep polls every live (Pending|InProgress) task with a `prNumber` — no separate subscription row, and no watch row that can show a misleading Failed (a circuit-breaker trip is a TASK outcome: only the task goes Blocked/Failed for the operator). The `EnqueueWatch` workflow stage is a graph placeholder (creates nothing); legacy pr-watch rows still in a queue are Closed by the sweep ("superseded") as it discovers them. Every watched PR gets a Reviewer-agent review (verdict recorded in the TASK's metadata `reviewSha`/`reviewVerdict`/`reviewNotes`/`reviewRound`; GitHub comment is the audit). Merge requires CI green (check runs, not legacy statuses) AND an approval (formal review or reviewer-agent at the current head). CI failure or changes-requested requeues the task for a rework round (same branch/PR, failure context in the prompt) — circuit breaker at `PRWatcher.MaxReworkAttempts` (3). A task showing Pending with a PR number is queued for a rework round (UI shows an R1/R2/R3 pill). The watch stale window anchors to `prOpenedAt` metadata. Non-blocking findings are NOT requested-changes: the reviewer approves and files them via `file_followup` (they re-enter through grooming like everything else).
- **Engineering concurrency is per-role, not global.** `Orchestrator/Slots/SlotTable.cs` holds a semaphore per (project, role); the dispatch loop acquires a role slot per ready sprint task (zero-timeout — a full pool skips only that role's tasks, other roles keep claiming). Caps come from the project's `roles_json` (`PUT /api/projects/{id}/roles`, live) falling back to `DefaultProjectRoles` (coredev/clientdev/reviewer=2, others=1). `spawner.maxConcurrentSessions` no longer gates dev dispatch. Reviewer work serializes inside the PRWatcher sweep (queue forms naturally).
- **Plan gate: engineering agents plan before they mutate (hard-enforced).** CoreDev/ClientDev runs get a `submit_plan` tool; the bash tool refuses commands the deterministic `ShellMutationClassifier` flags as mutating until the plan is approved. Gates are ordered per checkpoint (`Agents/Gates/`): deterministic first (`plan-schema`, `plan-territory`), LLM critic last (`plan-llm-review`, reviewer model config, fails open on outage). Resolution per checkpoint: DB override (memory key `gates/run/<checkpoint>`, future UI-managed) → `gates.run.<checkpoint>` config → built-in defaults. Mechanical rework rounds (conflict sync, infra retrigger) fast-path auto-approve. Revision budget 2, then the run fails structured. The audit trail lands in task metadata `planGate` (rendered on TaskDetail). Never weaken the tool-layer enforcement in favor of prompt-only instructions (operator rule 2026-07-26: hard gates wherever quality doesn't suffer). `Core/ModelRateLimitTracker.cs` keys cooldowns by the resolved provider+model — a minimax 429 must not freeze tasks pinned to another model (e.g. kimi for grooming/review/intake). ONE tracker instance is shared process-wide: `Agents/RateLimitAwareChatClient.cs` wraps every factory-built client, so a 429 from ANY subsystem (dev run, groomer, designer, reviewer sweep, intake, memory extractor) cools the model for ALL of them (Retry-After honored when present), cooling models fail fast client-side with a 429-patterned exception (no wasted HTTP), and a per-provider semaphore (`llm.maxConcurrentRequests`, default 2) caps simultaneous round-trips across all subsystems.
- **The Agents page is the agent control surface.** `/agents` shows per role: identity (name/type/territory/tools), the full role prompt (source: project override vs built-in), the effective provider+model (source: override | config | default), slot meter, the currently-running run (heartbeat: messages/tool calls/last-activity age) and the last finished run. The role catalog is canonical — `RoleAgentRegistry.All()` (engineering) + `RoleAgentRegistry.Pipeline` (scheduler-side) + `AllSlotRoles` (every pool) — so `/agents` and the project drill-down list the SAME 10 roles; pipeline model semantics are explicit (intake and triage have their own AgentType/editable model; designer/groomer/artist inherit coredev; orchestrator has no LLM). Model overrides are live and DB-backed (`Agents/RoleModelOverrides.cs`, memory keys `llm/roleModel/<AgentType>`; PUT/DELETE `/api/agents/roles/{name}/model`, incl. intake + triage) — the chat client factory and the run registry's model label consult them per run, no restart. Resolution order: override → `llm.roles` → provider default; a dangling override (provider removed) falls back to configured resolution. `MafAgentRunner` heartbeats `agent_run` after every model response (schema v21 `last_activity_at`) and persists the PARTIAL transcript on mid-run failure.
- **Each role owns its `ProjectSubdir`.** `CoreDev` → `PortHorizon.Core/`, `ClientDev` → `PortHorizon.Client/`, `QA` → read-only across the repo, `Reviewer` → read-only. The agent prompt bakes the path into the system prompt via `RoleAgentRegistry.cs`. **Plan-gate territory is project-configured only (operator rule, 2026-08-12):** the gate enforces `roles_json.$territory` (PUT `/api/projects/{id}/roles`); an unconfigured project is unconstrained (existence checks still run). The registry's built-in prefixes are Forge-repo-shaped prompt prose — never a gate fallback (talaria task-19/20 were rejected for naming `src/…` on a Forge-shaped fallback). The forge project carries its registry values in roles_json explicitly.
- **Don't auto-clear `Failed` issues.** `IssueStore.ReadyAsync` treats `Failed` blockers as intentionally open so the operator can investigate. The operator must explicitly close the issue or remove the `blocks` dep edge.
- **No manual out-of-loop fixes (operator rule, 2026-07-25).** When the pipeline can't handle a situation (conflicting PR, watchless PR, stuck watch), do NOT hand-merge, hand-push, or hand-patch around it. Either fix the system so the loop handles it (the operator would tell you to anyway), or surface it and let the operator direct. Manual steps leave dangling state (e.g. PR #32 was hand-created without a watch and sat invisible to review/merge).
- **Don't bypass `IssueStore` to write the queue.** All task state goes through `IssueStore.CreateAsync` / `ClaimAsync` / `TransitionAsync` / `UpdateMetadata`. Direct SQLite or `StateStore` writes corrupt the JSONL mirror and the recovery audit.
- **The JSONL mirror is a viewer artifact, not source of truth.** `IssueStore` wins on disagreement. `Core/IssuesJsonlMirror.cs` regenerates every 5s.
- **Schema changes: bump `CurrentSchemaVersion` in `Core/IssueStore.cs`, update BOTH DDL paths (SQLite migration chain in `InitializeSchemaSqlite`, SQL Server fresh-create in `InitializeSchemaSqlServer`), run `--check` after.** `CONTRIBUTING.md` § "Schema migrations" is authoritative.
- **Don't modify the architecture gate that `Reviewer` enforces.** See `agents/reviewer.md` for the exact rules. Don't weaken them in code or in the role prompt.
- **Cross-process safety: only one orchestrator per state directory.** SQLite WAL allows concurrent readers but a second writer waits up to the busy-timeout.

## CLI quick reference

`dotnet run --project Forge -- <flag>`:

- `--check` — pre-flight: config + DB schemas + GitHub + kilo gateway auth. No dispatch. Non-zero on any failure. Use for CI smoke and as the first step when stuck.
- `--status` — print queue summary and exit.
- `--once` — one dispatch cycle, then exit. Good for cron / trigger-driven dispatch.
- `--dashboard-only` — host the dashboard, do not dispatch. Do not add similar flags without sign-off.
- `--recover` — dry-run recovery: see what `StartupRecovery` would do. No side-effects.
- `--recover-and-start` — replay unfinished side-effects, then start dispatch.
- `--enqueue-task "<title>" --task-type <type> --task-desc "<desc>" --branch "<branch>"` — enqueue a task.
- `--migrate-db --target sqlserver [--connection-string "..."] [--include-open-work] [--reset]` — one-shot SQLite → Azure SQL state migration (registry + secrets ciphertext + memory keys; open work only with the flag). Idempotent. Stop the service first. See `docs/azure-sql-cutover.md`.
- `--init-azure-sql [--connection-string "..."] [--mi-name forge-mi]` — provision the contained DB user for the managed identity (db_owner) as the Entra admin. Idempotent.

Full flag list: `README.md` § "CLI".

## State files

All persisted state lives under `.portHorizon/`. Do not sprinkle app data outside that root. See `README.md` § "State files" for the layout; `docs/system-flow.md` § "Where state lives" for the storage-concern table.

In production (systemd), this maps to `/var/lib/forge/state/` via `StateDirectory=forge`. See `docs/linux-deployment.md`.

## When stuck

1. `dotnet run --project Forge -- --check`. If it passes, the bug is in the dispatch path. If it fails, the failure message names the subsystem to investigate.
2. The **Flow page** (`/flow`) shows the live pipeline DAG — planning lane (specs/ad-hoc) vs implementation lane (tasks), per-node counts, and a per-issue journey view (`/flow?issue={id}`) derived from the `issue_event` timeline. First stop for "where is my work stuck?". **Edit mode** (`/flow?mode=edit`) is the workflow control surface: the pipeline is a `WorkflowDefinition` (`Core/Workflow/`, built-in default = the previously hardcoded DAG) edited as draft → validated publish → memory-key override (`workflow/live`, snapshots under `workflow/versions/`). Wiring & policy edits only — the transition table stays code-owned; gates/policies/step-toggles/branch options resolve per evaluation, no restart.
2. `tail -f` `.portHorizon/state/issues.jsonl` for live queue state (or `sudo journalctl -u forge -f` on the host).
3. `<dataRoot>/logs/agent.log` — per-run diagnostic: message roles, text lengths, tool-call names per agent run. First stop when a run "completes" with no diff. (minimax-m3 quirk: it can emit a tool call as literal text markup `]<]minimax[><tool_call>...`; `MafAgentRunner` detects the leak and nudges the model to continue, bounded at 3.)
4. The dashboard `Events` tab streams `DashboardEvent` over SSE.
5. `Orchestrator/StartupRecovery.cs` + `docs/p4-restart-safety.md` for crash-recovery questions.
6. `sudo systemctl status forge` / `sudo journalctl -u forge -n 200 --no-pager` for service-level issues (systemd mode).

## Linux deployment

The orchestrator runs under systemd (`Type=notify`) on Linux.
See `docs/linux-deployment.md` for the install / upgrade / ops
runbook. The unit template is `deploy/systemd/forge.service`; the
install scripts are `scripts/install-systemd-service.sh` and
`scripts/uninstall-systemd-service.sh`. Self-deploy uses
`DeploymentKind.SelfHostedSystemdService`
(`DeploymentPipeline/SelfHostedSystemdServiceDeploymentExecutor.cs`),
which repoints `/opt/forge/current` and runs `systemctl restart
forge` — synchronous, no detached helper needed (unlike the
historical Windows-SCM path that used `tools/Forge.Deployer`).

## Project model (DB-only registry, project-agnostic)

Forge is project-agnostic — there is no hardcoded "PortHorizon"
assumption in the codebase. Projects are registered in the SQLite
`project` table (schema v17; v18 added the per-project `secret`
table, v19 added `project.roles_json` for DB-persisted role
caps), surfaced in the dashboard, and picked up by the dispatch
loop. The `appsettings.json` `projects[]` array
is **deprecated** — it is no longer the source of truth. Add
projects via:

- **Dashboard**: `Projects` page → **Add Project** button. Guided
  flow: enter a git token → **Fetch repositories** (lists what the
  token can see via `POST /api/projects/lookup/repos`) → pick a repo
  (auto-fills URL/name/id and detects the default branch via
  `POST /api/projects/lookup/branches`) → override the branch if
  needed → Register. The entered token is stored encrypted as the
  project's `github_token` secret. Manual URL entry still works.
  Default-branch resolution order: (1) git token, (2) the remote's
  HEAD symref, (3) the clone's `origin/HEAD` pull info, (4) user
  override — which always wins.
- **API**: `POST /api/projects/` with the same JSON shape (`id`,
  `name`, `repoUrl`, optional `defaultBranch` override, optional
  `gitToken` — stored as the project's `github_token` secret before
  cloning). The endpoint calls `ProjectCloner.CloneAsync` inline
  (best-effort — clone failures don't roll back the registration;
  the operator fixes the PAT/repo and retries via
  `POST /api/projects/{id}/sync`).

Other project-lifecycle endpoints:

- `DELETE /api/projects/{id}` — remove from registry. Does NOT
  delete the local clone or worktrees (operator-managed cleanup).
- `POST /api/projects/{id}/sync` — `git pull --ff-only` the default
  branch. Updates `last_synced_at` + `last_sync_error` in SQLite.

Auth model (private repos): the per-project `github_token` secret
wins wherever a project id is known (`Projects/GitHubTokenResolver.cs`
— clone/sync/endpoints/bootstrap, and the dispatch bundle for
push/PR); the global `GITHUB_TOKEN` env var / `github.token` config
is the fallback. The token is injected into the clone URL only, then
the stored `origin` is reset to the clean form immediately, and a
credential-store file (mode 0600) is written under
`<localPath>/.forge/git-credentials` for future `git push` / `pull`.
If a registration-time clone fails, the bootstrap scaffolds a local
git repo (keeps the system usable); `POST /api/projects/{id}/sync`
(and every service boot) reconciles that scaffold — adds `origin`,
installs the credential helper, fetches, and aligns the branch to
`origin/<defaultBranch>` — once a working PAT is available.

**First goal: build Forge with Forge.** Add a `forge` entry
(id=forge, repoUrl=https://github.com/Xyrces/Forge.git) via the UI
or API, then restart the orchestrator so the dispatch loop sees
it. The v1 dispatch loop picks the first registered project as
its primary; runtime hot-add of dispatch targets is a planned
follow-up.

## Where to add follow-up skills / rules (this very plan's "out of scope" items)

When future agents hit a recurring need, drop a skill in `.kilo/skills/<name>/SKILL.md` with `name` and `description` frontmatter:

- Schema migrations skill → `.kilo/skills/forge-schema-migrations/SKILL.md`. Mirror `CONTRIBUTING.md` § "Schema migrations".
  - Agent prompts skill → `.kilo/skills/forge-agent-prompts/SKILL.md`. Cover `Agents/RoleAgentRegistry.cs`, `IAgent.AgentType`, `agents/<role>.md` frontmatter, `FromTaskType` mapping.
- Intake→Product→Designer→Artist→Groomer→Engineering pipeline skill → `.kilo/skills/forge-pipeline/SKILL.md`. The six-stage spec status machine; `docs/system-flow.md` § "Pipeline" is the source.
- Slash commands → `.kilo/command/<name>.md` once a concrete operator workflow needs them.
- Kilo subagent definitions → `.kilo/agent/<name>.md` (singular) when delegation becomes useful.

The skills that ship today:

- `.kilo/skills/forge-architecture/SKILL.md` — navigation map.
- `.kilo/skills/forge-task-lifecycle/SKILL.md` — the engineering dispatch pipeline (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch) + `PRWatcher`.
- `.kilo/skills/forge-recovery/SKILL.md` — P4 Stage A in-process recovery vs P4 Stage B Durable Task Scheduler sidecar.
- `.kilo/skills/forge-secrets/SKILL.md` — the per-project secrets system: encrypted storage, the two-panel Secrets page, and the by-reference consumption model (`FORGE_SECRET_*` env vars in the agent bash tool; values never enter LLM context).