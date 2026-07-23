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

## Module boundaries (non-negotiable)

Lifted from `CONTRIBUTING.md` § "Boundaries":

- `Core/` has no I/O beyond SQLite. No HTTP, no GitHub, no LLM. Stores take their paths via the constructor; they don't read env vars.
- `Agents/` depends on `Core/`, `Configuration/`, `Dashboard/`. Publishes `DashboardEvent`. Does not read `appsettings.json` directly.
- `Orchestrator/` depends on `Agents/` + `Core/`. Glues stores + agent + git + GitHub.
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

## Agent-specific rules (the ones an LLM agent most often gets wrong)

- **Engineer agents must not open a PR.** The orchestrator's `OrchestratorAgent.DispatchSingleTaskAsync` opens the PR via `GitHubService.CreatePullRequestAsync`. The agent pushes the branch and stops. This is asserted in `BuildPrompt` (`OrchestratorAgent.cs:314`).
- **Sprint flow: ALL engineering work happens inside a sprint.** The `SprintAssembler` (5-min tick) completes the Active sprint when its tasks are terminal and assembles + activates the next from eligible Pending tasks (groomed-spec groups FIFO via the task→story→spec parent chain; ad-hoc tasks last). `OrchestratorAgent` never dispatches a dev task that isn't linked to the Active sprint (watches are exempt — they're lifecycle, not sprint work). There is deliberately no UI button to create sprints. Agent runs inside a sprint get the sprint goal + sibling roster in the prompt and shared `sprint/{id}/` memory keys.
- **Review loop is automatic.** Every watched PR gets a Reviewer-agent review (verdict recorded in watch metadata; GitHub comment is the audit). Merge requires CI green (check runs, not legacy statuses) AND an approval (formal review or reviewer-agent at the current head). CI failure or changes-requested requeues the task for a rework round (same branch/PR, failure context in the prompt) — circuit breaker at `PRWatcher.MaxReworkAttempts` (3), then Blocked/Failed for the operator. A task showing Pending with a PR number is queued for a rework round (UI shows an R1/R2/R3 pill).
- **Each role owns its `ProjectSubdir`.** `CoreDev` → `PortHorizon.Core/`, `ClientDev` → `PortHorizon.Client/`, `QA` → read-only across the repo, `Reviewer` → read-only. The agent prompt bakes the path into the system prompt via `RoleAgentRegistry.cs`.
- **Don't auto-clear `Failed` issues.** `IssueStore.ReadyAsync` treats `Failed` blockers as intentionally open so the operator can investigate. The operator must explicitly close the issue or remove the `blocks` dep edge.
- **Don't bypass `IssueStore` to write the queue.** All task state goes through `IssueStore.CreateAsync` / `ClaimAsync` / `TransitionAsync` / `UpdateMetadata`. Direct SQLite or `StateStore` writes corrupt the JSONL mirror and the recovery audit.
- **The JSONL mirror is a viewer artifact, not source of truth.** `IssueStore` wins on disagreement. `Core/IssuesJsonlMirror.cs` regenerates every 5s.
- **Schema changes: bump `CurrentSchemaVersion` in `Core/IssueStore.cs`, use `CREATE TABLE IF NOT EXISTS`, run `--check` after.** `CONTRIBUTING.md` § "Schema migrations" is authoritative.
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

Full flag list: `README.md` § "CLI".

## State files

All persisted state lives under `.portHorizon/`. Do not sprinkle app data outside that root. See `README.md` § "State files" for the layout; `docs/system-flow.md` § "Where state lives" for the storage-concern table.

In production (systemd), this maps to `/var/lib/forge/state/` via `StateDirectory=forge`. See `docs/linux-deployment.md`.

## When stuck

1. `dotnet run --project Forge -- --check`. If it passes, the bug is in the dispatch path. If it fails, the failure message names the subsystem to investigate.
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

- **Dashboard**: `Projects` page → **Add Project** button.
  Fields: `id` (lowercase slug, becomes the dispatch key),
  `name` (display label), `repoUrl` (HTTPS or SSH), `defaultBranch`.
- **API**: `POST /api/projects/` with the same JSON shape. The
  endpoint calls `ProjectCloner.CloneAsync` inline (best-effort
  — clone failures don't roll back the registration; the operator
  fixes the PAT/repo and retries via `POST /api/projects/{id}/sync`).

Other project-lifecycle endpoints:

- `DELETE /api/projects/{id}` — remove from registry. Does NOT
  delete the local clone or worktrees (operator-managed cleanup).
- `POST /api/projects/{id}/sync` — `git pull --ff-only` the default
  branch. Updates `last_synced_at` + `last_sync_error` in SQLite.

Auth model (private repos): PAT is read from `GITHUB_TOKEN` env var
or `github.token` in appsettings.json; injected into the clone URL
only, then the stored `origin` is reset to the clean form
immediately, and a credential-store file (mode 0600) is written
under `<localPath>/.forge/git-credentials` for future `git push` /
`pull`.

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