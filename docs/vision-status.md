# Vision status

Where each phase of `docs/agent-framework-design.md` actually stands as of 2026-07-02. This is the source of truth for "what's done, what's not, what's deferred." Updated by the commit that lands the work — keep it current.

| Phase | Goal | Status | Landed in | Notes |
|---|---|---|---|---|
| **P0** | MAF packages + IAgentRunner scaffolding; drop the `kilo serve` / `kilo acp` subprocess dependency | ✅ done | `6b4b459` | Closed 2026-06-30. 96 tests passing. |
| **P0.5** | vision table + vision import + Vision UI tab | ❌ not started | — | Workaround: add a `vision/*` memory key. See [vision-validation.md](vision-validation.md). |
| **P1** | skills loading into MAF agent instructions | ✅ done | `598b377` | `SqliteSkillSource` reads `.kilo/agents/*.md` + the `skills` table, injects into `ChatClientAgent` instructions. |
| **P1.4** | Intake agent (HarnessAgent, persistent per project, Intake tab UI) | ✅ done | `f8f9329` | Intake tab + IntakeAgentRegistry + session→specs lookup. |
| **P1.5.a** | Spec tab read-only + Spec CRUD endpoints | ✅ done | `3b81ef5` | Spec tab + `/api/specs` REST API. |
| **P1.5.b** | Product agent writes specs via AIFunctions (human-gated approval) | ✅ done | `b8a65df` | ProductAgent + `SpecStatus.Grooming` introduced; subsequent PRs added GroomerAgent that operates on Approved specs. |
| **P2.a** | Designer agent + `design_artifact` table | ❌ not started | — | The system cannot dispatch design tasks. Largest single gap. |
| **P2.b** | Artist agent + `art_output` table + MeshyClient integration | ❌ not started | — | The system cannot dispatch art tasks. |
| **P3** (existing) | Engineering dispatch becomes a MAF workflow | 🟡 partial | `680c412`–`d677a78` | Executors + `EngineeringDispatchWorkflow` built and tested. Orchestrator's `DispatchSingleTaskAsync` still uses sequential code; swap is a refactor. |
| **P3.5** | issue_groomer_run table + scheduled Groomer + manual Re-run button | 🟡 partial | — | GroomerAgent is on-demand via `POST /api/specs/{id}/groom`. No schedule, no `issue_groomer_run` table. |
| **P4** | Durable execution via `Microsoft.Agents.AI.Hosting` | ❌ not started | — | In-process reaper (`Spawner.StaleMinutes`) is the safety net. |

## Test count

| Snapshot | Total | Pass | Skip | Source |
|---|---|---|---|---|
| Pre-P0 | 0 | 0 | 0 | `6b4b459` initial |
| Post-P0 | 96 | 96 | 0 | `6b4b459` |
| Post-Phase 2 (dep graph) | 240 | 240 | 0 | `d79bad9` |
| Post-Phase 3 (memory) | 281 | 281 | 2 | `29bb7d3` |
| Post-Phase 4 (JSONL) | 286 | 286 | 2 | `71f87a9` |
| Post-Phase 5 (StateStore.Tasks removal) | 281 | 281 | 2 | `8ed2242` |
| Post-P3 (workflow executors) | 294 | 294 | 2 | `d677a78` |
| Today | **294** | **294** | **2** | this commit |

(The 2 skipped tests are `RealLlmIntegrationTests` — they require a kilo gateway API key + the model id to be in the JWT's org. They run as part of CI in any environment with a configured key.)

## What works end-to-end today

A new operator can, after following [README.md](../README.md) and [install-kilo.md](../install-kilo.md):

1. Install — `dotnet build` + `appsettings.json`. No separate CLI install.
2. Run — `dotnet run --project PortHorizon.Agents`.
3. Queue a task — CLI, HTTP, or dashboard. The orchestrator claims, creates a worktree, runs the agent (with bash AIFunction + memory recall), commits, pushes, opens a PR, enqueues a watch.
4. Observe — live dashboard with Tasks, Spec, Intake, Memory, Events tabs; JSONL mirror for `tail -f`; SSE event stream; per-issue dependency graph.
5. Steer — add memory, add dep edges, force-transition a stuck task, approve a PR to unblock the watch.
6. Stay out of the way — stale `InProgress` tasks reaped at startup; one retry before `Failed`; `Failed` is intentionally not auto-cleared so the operator can investigate.

## What doesn't work (yet) — gap list

Each row maps to a phase above. The "blocker" column is who / what is preventing the work from being cheap.

| Gap | Blocker | Work estimate |
|---|---|---|
| **P0.5** Vision import + Vision tab | — | small — half a day: read `vision.md` at startup, parse it, surface in a tab. |
| **P2.a** Designer agent | depends on what "design" means for a game like PortHorizon (level layout, system architecture, gameplay mechanics). New agent code + new tool surface + new tab. | medium — needs design call first. |
| **P2.b** Artist agent + MeshyClient | Meshy API key + integration; sprite + 3D model pipelines. | medium-to-large — 1-2 weeks for a focused builder. |
| **P3** workflow wired in | swap `DispatchSingleTaskAsync` to use `EngineeringDispatchWorkflow`. Already 80% built; just behavioral parity on `AlreadyClaimed`. | small — half a day. |
| **P3.5** scheduled Groomer + `issue_groomer_run` | the scheduler itself is small (an `IHostedService`); the table is small. | small — half a day. |
| **P4** DurableTask | adding a new orchestrator runtime; the existing one is in-process. | medium-to-large — 1-2 weeks; major change. |

## Conventions

When a phase lands:
1. Commit the work as `[phase-name] description`.
2. Update this table — set the status emoji, add a commit link, drop a one-liner.
3. Update [vision-validation.md](vision-validation.md) if the gap list changes materially.
4. Update the test count row at the bottom.

A phase moving from 🟡 partial to ✅ done is the trigger for a version bump in the design doc's "Implementation plan" table.
