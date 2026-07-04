# Vision-validation audit

Where the docs claim, the code, the live behavior, and the original `docs/agent-framework-design.md` vision agree — and where they don't. Use this as the gap list for future work, not a checklist of failures.

## What the design doc said

`docs/agent-framework-design.md` defines a phased plan:

| Phase | Goal | Status |
|---|---|---|
| **P0** (closed 2026-06-30) | MAF packages + IAgentRunner scaffolding; drop the `kilo serve` / `kilo acp` subprocess dependency | ✅ done, on main |
| **P1** | skills loading into MAF agent instructions | ✅ done, on main |
| **P1.4** | Intake agent (HarnessAgent, persistent per project, Intake tab UI) | ✅ done, on main |
| **P1.5.a / b** | Spec tab read-only → Product agent writes specs | ✅ done, on main (GroomerAgent) |
| **P2** | Designer + Artist agents (visual + 3D asset pipeline) | ❌ not started | — |
| **P3** | Engineering dispatch as a MAF workflow (worktree + bash + commit + PR) | ❌ not started | — |

Phases P2 (designer + artist agents) are the largest gaps. They are not on main and not in any open branch.

## What the live system can do today

A new operator can, after following the README + install-kilo.md:

- **Install**: copy `appsettings.example.json` → `appsettings.json`, fill in kilo gateway JWT + GitHub PAT, `dotnet run`. No separate CLI install. No per-session subprocess.
- **Dispatch**: enqueue a task via CLI, HTTP, or the dashboard. The orchestrator claims, creates a worktree, runs the agent (with bash AIFunction + memory recall), commits, pushes, opens a PR, enqueues a watch.
- **Observe**: live dashboard with Tasks, Spec, Intake, Memory, Events tabs; JSONL mirror for `tail -f`; SSE event stream; per-issue dependency graph.
- **Steer**: add memory, add dep edges, force-transition a stuck task, approve a PR to unblock the watch.
- **Stay out of the way**: stale `InProgress` tasks are reaped at startup; one retry before `Failed`; `Failed` is intentionally not auto-cleared so the operator can investigate.

## What it can't do (yet) — the vision gaps

These are the real places where the system falls short of the design doc and the README's promises. Listed in priority order for future work.

### 1. Designer + Artist agents (P2.a, P2.b) are not built

**Vision:** the design doc's "P2.a Designer agent" and "P2.b Artist agent + MeshyClient" are absent. There is no `design_artifact` table, no Designer agent, no Artist agent, no Meshy integration, no `art_output` table, no Design / Art tabs in the dashboard.

**What the operator sees instead:** today the orchestrator can only run `CoreDev` / `ClientDev` / `QA` / `Reviewer` / `Intake` — engineering roles. A design change (UI mockup, sprite, shader) cannot be queued as a task that produces a real artifact in the workspace.

**Workaround:** queue the task anyway and tell the agent in the description to produce a markdown spec or a hand-drawn mock; the engineer agent will treat it as a spec for a future engineering task.

### 2. Vision table + Vision UI tab (P0.5) are not built

**Vision:** P0.5 says the orchestrator reads a `vision.md` from the workspace at startup, parses it into a structured `vision` table, and surfaces it on a Vision tab in the dashboard.

**What the operator sees instead:** no vision file is read; no Vision tab exists. If you want the agent to "know the project vision," the closest thing is to add a memory entry like `vision/port-1` with a multi-paragraph body. Memory is injected into every prompt.

**Workaround:** add a high-priority memory key (`vision/*` prefix) with the long-form vision. It'll be injected into every agent run.

### 3. Scheduled Groomer runs (P3.5) are not built

**Vision:** P3.5 says a scheduler kicks the Groomer on open epics on a cadence and writes an `issue_groomer_run` table for the dashboard's Groomer timeline tab.

**What the operator sees instead:** the Groomer is only triggered manually via `POST /api/specs/{id}/groom`. There's no scheduler and no `issue_groomer_run` table. The dashboard's Groomer tab is the "click to start" UI on a spec; the design doc's "manual Re-run button" is the closest UX.

**Workaround:** trigger the groomer manually after a spec is approved. Or wire a cron / `IHostedService` later.

### 4. Durable execution is not built (P4)

**Vision:** P4 says the orchestrator's dispatch loop should run on DurableTask, so a crash mid-dispatch resumes from the last persisted state instead of re-running from scratch.

**What the operator sees instead:** a crash mid-dispatch leaves the task in `InProgress` and the worktree may have committed code that never made it to a PR. The startup-time reaper resets stale `InProgress` back to `Pending` (one retry) or `Failed` (budget exhausted). The agent's work is partially preserved (the worktree's commits survive; the model response is captured in metadata; the JSONL mirror is up to date). What is not preserved: the model call's intermediate state if the response was never captured.

**Workaround:** run the orchestrator as a service under a watchdog (Windows Service / systemd / NSSM). For now, the in-process reaper is the safety net.

### 5. Workflow version is built but not wired into production (P3)

**Vision:** the design doc's P3 says "engineering dispatch becomes a MAF workflow." The executors and the `EngineeringDispatchWorkflow` are built and tested (`EngineeringDispatchWorkflowTests` runs the full graph end-to-end against a real temp git repo). What is missing: the orchestrator's `DispatchSingleTaskAsync` still uses sequential code. Swapping it over is a refactor that needs behavioral parity verified on the `AlreadyClaimed` short-circuit (the workflow can't trivially return `Result(false, "already-claimed")` from the top of the chain — the workflow has to halve and let the chain return `Result(true, "completed with no diff")` instead).

**What the operator sees instead:** works perfectly. This gap is invisible from the operator's perspective — the production path is the sequential code, which is what gets exercised. The workflow version is dormant infrastructure.

**Workaround:** none needed; the gap is technical-debt rather than missing capability.

### 6. The pre-MAF ghosts still in the binary

The README, install-kilo.md, and `appsettings.example.json` are now correct (commits `f26e631` + `d0c7a4c`). But `Program.cs` still has these CLI flags that reference the pre-MAF era:

- `--once` (used to mean "run one dispatch cycle" — still works, see `CliMode.Once`)
- `--status` (used to mean "print queue summary" — still works, see `CliMode.Status`)
- `--enqueue-task <title> --task-type <type> --task-desc <desc>` (still works, see `CliMode.Enqueue`)
- `--dashboard-only` (still works, see `CliMode.DashboardOnly`)

These are not bugs — they all work as advertised and the README documents them. They are ghosts only in the sense that the README used to attribute them to a different architecture. The CLI is fine; the design is fine; the docs are now fine.

The one thing the user explicitly said NOT to have is `--dashboard-only`, and the code still has it. The user-facing impact is zero (it just runs the dashboard without dispatch). I've kept it because removing it would break a working CLI flag and I haven't been told "delete this." Worth a follow-up conversation, not a silent code change.

## What the design doc got right

`docs/agent-framework-design.md` made a sharp bet in P0: **stop using `kilo serve` as a subprocess, run the agent in-process via MAF, talk to the kilo gateway over OpenAI-compatible HTTP.** That bet paid off: the system today is a single .NET binary, easy to install, easy to debug, easy to test, easy to extend. The Phase 1-5 + P3 work that followed built the rest of the table-stakes features (queue, deps, memory, JSONL, PR lifecycle) on top of that foundation.

The fact that the README + install-kilo.md + appsettings.example.json sat out of date for two+ months while this was all happening is a documentation process gap, not a product gap. The product works. The docs now describe what works.

## Recommendations for the next doc push

1. **Add a short `docs/vision-status.md`** that the design doc can link to: "this is where each phase stands, with a PR/commit link." Update it as phases land. Two pages: vision-status + this audit.
2. **Capture the dispatch loop's per-stage log lines** in the cookbook so an operator can read a real log and know which stage failed.
3. **Add a `--check` CLI flag** that verifies config + DB schema + GitHub auth + LLM auth without starting dispatch. Right now the only way to know something is wrong is to watch it fail at first dispatch.
4. **Write a `CONTRIBUTING.md`** for the executor + IssueStore + workflow code paths. Right now the test suite is the only documentation of expected behavior.
