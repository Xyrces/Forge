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
3. The dashboard `Events` tab streams `DashboardEvent` over SSE.
4. `Orchestrator/StartupRecovery.cs` + `docs/p4-restart-safety.md` for crash-recovery questions.
5. `sudo systemctl status forge` / `sudo journalctl -u forge -n 200 --no-pager` for service-level issues (systemd mode).

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

## Project model (project-agnostic, self-build capable)

Forge is project-agnostic — there is no hardcoded "PortHorizon"
assumption in the codebase. Each project is defined by a Git URL +
default branch:

- `ProjectOptions.RepoUrl` — the canonical identifier. Set this on
  the project entry in `appsettings.json` `projects[]` (one-time
  seed) or via `POST /api/projects` (runtime add).
- `ProjectCloner.CloneAsync` clones into `<dataRoot>/projects/<id>/`
  on first boot. PAT for private repos comes from `GITHUB_TOKEN`
  env var or `github.token` in appsettings; the PAT is injected
  into the clone URL only (the stored `origin` is reset to the
  clean form immediately after, and a credential-store file is
  written for future `git push`/`pull`).
- `ProjectStore` (schema v17, SQLite table `project`) is the
  runtime-mutable registry. `appsettings.json` `projects[]` is
  seeded into it on first boot via
  `ProjectRegistryLoader.SeedAsync` (idempotent — re-runs don't
  overwrite operator edits).
- Worktrees are created off `<dataRoot>/projects/<id>/` (the
  cloned repo) — `git worktree add` semantics unchanged.

**First goal: build Forge with Forge.** Add a `forge` project entry
with `repoUrl: "https://github.com/Xyrces/Forge.git"` and Forge
will clone itself, run agents on it, and rebuild + restart on
deployment approval. See `appsettings.example.json` for the
canonical example.

## Where to add follow-up skills / rules (this very plan's "out of scope" items)

When future agents hit a recurring need, drop a skill in `.kilo/skills/<name>/SKILL.md` with `name` and `description` frontmatter:

- Schema migrations skill → `.kilo/skills/forge-schema-migrations/SKILL.md`. Mirror `CONTRIBUTING.md` § "Schema migrations".
  - Agent prompts skill → `.kilo/skills/forge-agent-prompts/SKILL.md`. Cover `Agents/RoleAgentRegistry.cs`, `IAgent.AgentType`, `agents/<role>.md` frontmatter, `FromTaskType` mapping.
- Intake→Product→Designer→Artist→Groomer→Engineering pipeline skill → `.kilo/skills/forge-pipeline/SKILL.md`. The six-stage spec status machine; `docs/system-flow.md` § "Pipeline" is the source.
- Slash commands → `.kilo/command/<name>.md` once a concrete operator workflow needs them.
- Kilo subagent definitions → `.kilo/agent/<name>.md` (singular) when delegation becomes useful.

The three skills that already ship with this plan:

- `.kilo/skills/forge-architecture/SKILL.md` — navigation map.
- `.kilo/skills/forge-task-lifecycle/SKILL.md` — the engineering dispatch pipeline (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch) + `PRWatcher`.
- `.kilo/skills/forge-recovery/SKILL.md` — P4 Stage A in-process recovery vs P4 Stage B Durable Task Scheduler sidecar.