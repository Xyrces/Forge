# Vision status

Where each phase of `docs/agent-framework-design.md` actually stands as of 2026-07-02. This is the source of truth for "what's done, what's not, what's deferred." Updated by the commit that lands the work — keep it current.

| Phase | Goal | Status | Landed in | Notes |
|---|---|---|---|---|
| **P0** | MAF packages + IAgentRunner scaffolding; drop the `kilo serve` / `kilo acp` subprocess dependency | ✅ done | `6b4b459` | Closed 2026-06-30. 96 tests passing. |
| **P0.5** | vision table + vision import + Vision UI tab | ✅ done | this commit + `vision/master` memory key | `VisionStore` reads `docs/MASTER_DESIGN.md` at startup, surfaces it via `GET /api/vision` + the dashboard's Vision tab, and injects the content into memory under `vision/master` so every agent prompt includes it. Operator edits the file + clicks Refresh in the dashboard (or restarts) to update. |
| **P1** | skills loading into MAF agent instructions | ✅ done | `598b377` | `SqliteSkillSource` reads `.kilo/agents/*.md` + the `skills` table, injects into `ChatClientAgent` instructions. |
| **P1.4** | Intake agent (HarnessAgent, persistent per project, Intake tab UI) | ✅ done | `f8f9329` | Intake tab + IntakeAgentRegistry + session→specs lookup. |
| **P1.5.a** | Spec tab read-only + Spec CRUD endpoints | ✅ done | `3b81ef5` | Spec tab + `/api/specs` REST API. |
| **P1.5.b** | Product agent writes specs via AIFunctions (human-gated approval) | ✅ done | `b8a65df` | ProductAgent + `SpecStatus.Grooming` introduced; subsequent PRs added GroomerAgent that operates on Approved specs. |
| **P2.a** | Designer agent + `design_artifact` table + visual-language rules | ✅ done | `60e3e62`–`c15587c` | `DesignArtifact` + `DesignerRunStore` (SQLite, schema v9). `DesignHygieneChecker` runs 10 deterministic rules before the LLM. `DesignerAgent` is a MAF ChatClientAgent with 6 AIFunctions; calls `db_set_spec_status` to transition Draft → Designed/Approved/NeedsRevision. `DesignerScheduler` (background, 5-min tick). `POST /api/specs/{id}/design` (manual). `GET /api/designer/runs` + `GET /api/specs/{id}/design-artifacts` (timeline). Design tab in the dashboard. Engineering agent prompt now references `design_artifact` ids. The Groomer gate widens to `Designed | Approved | Groomed`. The Intake → Product → Designer → Groomer → Engineering pipeline is wired end-to-end (the existing `ProductRefinementQueue` was built but never started — that was the dead-code finding). Live-verified: a single Designer run against the kilo gateway produced 2 design artifacts (a wireframe + a visual-rule) and transitioned a UI spec to NeedsRevision in 2.5 min. The operator-maintained `Xyrces/godot-ecs-gamedev-playbook` is wired into agent memory via `SkillBootstrap` (idempotent `playbook/*` keys) so every agent prompt includes a per-role skill list and the repo URL. |
| **P2.b** | Artist agent + `art_output` table + MeshyClient integration | ✅ done | `0f0d8e8`–`6369ac3` | `ArtOutput` + `ArtistRunStore` (SQLite, schema v10). `MeshyClient` covers text-to-3d, image-to-3d, multi-image-to-3d, rigging; long-polls task status; downloads the resulting `.glb` to `.portHorizon/art-output/{spec}/{art-id}.glb`. `ArtistAgent` is a MAF ChatClientAgent with 6 AIFunctions (`db_get_spec`, `db_get_design_artifacts`, `db_get_visual_language`, `db_submit_meshy_job`, `db_save_art_output`, `db_set_spec_status`). Calls `db_set_spec_status` to transition Designed → AssetReady / NeedsRevision. `ArtistScheduler` (background, 5-min tick). `POST /api/specs/{id}/design-art` (manual). `GET /api/artist/runs` + `GET /api/specs/{id}/art-output` + `GET /api/art-output/{id}/file` (timeline + file stream). Art tab in the dashboard renders GLB via `<model-viewer>` + PNG via `<img>` + MP4 via `<video>`. Engineering agent prompt now references `art_output` ids. The Groomer gate widens to `Designed | AssetReady | Approved | Groomed`. The `AssetReady` state is inserted between `Designed` and `ReadyForGroom`. Live-verified: a single Meshy text-to-3d job against the live API (`api.meshy.ai`, Meshy 6 model) submitted task `019f289f-...`, polled to `SUCCEEDED` in 2m12s, downloaded a 32 KB `.glb` with the glTF magic `0x46546C67`. The Designer → Artist → Groomer → Engineering pipeline is now end-to-end with both visual and asset layers. |
| **P3** (existing) | Engineering dispatch becomes a MAF workflow | ✅ done | `680c412`–`8ac63ec` | Executors + `EngineeringDispatchWorkflow` + orchestrator wired. Dispatch is now Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch as typed `FunctionExecutor<TIn, TOut>` instances. Live-verified with task-6 (15.6s end-to-end, modelResponse captured in metadata). |
| **P3.5** | issue_groomer_run table + scheduled Groomer + manual Re-run button | ✅ done | this commit | `IssueGroomerRunStore` (SQLite, schema v8) + `ScheduledGroomer` (wakes up every 5 min, grooms Approved specs that haven't been groomed recently or whose last groom failed). Manual groom via `POST /api/specs/{id}/groom` writes the same table with trigger=`manual`. Dashboard reads via `GET /api/groomer/runs?specId=...&limit=...`. |
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
| Post-P3 (orchestrator wired) | 294 | 294 | 2 | `8ac63ec` |
| Post-P0.5 (vision) | 297 | 297 | 2 | `787c89a` |
| Post-P3.5 (scheduled groomer) | 302 | 302 | 2 | `e73f2c0` |
| Post-P2.a (Designer pipeline, 7 steps) | 321 | 321 | 2 | `0c39532` |
| Post-P2.a (playbook bootstrap + scheduler/endpoint tests) | 336 | 336 | 2 | `c15587c` |
| Post-P2.b (Artist + MeshyClient) | 364 | 364 | 2 | `6369ac3` |
| Today | **364** | **364** | **2** | this commit |

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
| **P0.5** Vision import + Vision tab | ~~—~~ | ~~small — half a day: read `vision.md` at startup, parse it, surface in a tab.~~ Done. |
| **P2.a** Designer agent | ~~depends on what "design" means for a game like PortHorizon~~ Done in `0c39532`. | ~~medium — needs design call first.~~ |
| **P2.b** Artist agent + MeshyClient | Meshy API key + integration; sprite + 3D model pipelines. | medium-to-large — 1-2 weeks for a focused builder. |
| **P3** workflow wired in | ~~swap `DispatchSingleTaskAsync` to use `EngineeringDispatchWorkflow`.~~ Done in `8ac63ec`. | ~~small — half a day.~~ |
| **P3.5** scheduled Groomer + `issue_groomer_run` | ~~the scheduler itself is small (an `IHostedService`); the table is small.~~ Done. | ~~small — half a day.~~ |
| **P4** DurableTask | adding a new orchestrator runtime; the existing one is in-process. | medium-to-large — 1-2 weeks; major change. |

## Conventions

When a phase lands:
1. Commit the work as `[phase-name] description`.
2. Update this table — set the status emoji, add a commit link, drop a one-liner.
3. Update [vision-validation.md](vision-validation.md) if the gap list changes materially.
4. Update the test count row at the bottom.

A phase moving from 🟡 partial to ✅ done is the trigger for a version bump in the design doc's "Implementation plan" table.
