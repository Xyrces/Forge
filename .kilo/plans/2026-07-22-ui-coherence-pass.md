# Sprint-centric execution + spec drill-down + backlog clarity

## Operator correction (2026-07-22, authoritative)

> There was never supposed to be a UI button for the sprint. ALL work should be part of a sprint, even if it's only for the single item. The point was to group similar work into goals and give agents shared memory within a sprint. Once a spec is turned into an epic and that epic is groomed into stories, it is then eligible for ingest into a sprint. We build each sprint at the completion of the last. Only once a sprint is started can agents begin working on it. Everything before that point is design and high-level planning.

The sprint flow is the fundamental execution model — not an operator artifact. The previous plan's P3 (manual propose button) is scrapped.

## Current state (verified)

- `SprintStore` is complete: `CreateAsync`, `SetActiveAsync`, `AddIssueAsync`, `GetIssueIdsAsync`, statuses `Active|Completed|Archived`, `sprint_issue` linkage table. **Zero sprints ever created.**
- `IssueStore.ReadyAsync(limit, sprintId)` already filters to sprint members via `sprint_issue` when sprintId is non-null; **null = ungated** (returns all Pending).
- `OrchestratorAgent` calls `ReadyAsync(0, activeSprint?.Id)` — with no active sprint this dispatches *everything* (today's behavior).
- `SprintProposeService` (scoring + audit + `CommitAsync`) exists for operator-proposal; the assembler below does deterministic epic-grouped assembly instead. Keep the proposer untouched (future use).
- Memory: `MemoryRecord(Key, Body, …)`; `MemoryExtractor` namespaces keys by issue id; `MafAgentRunner.BuildMemoryInstructionsAsync` recalls with `keyPrefix: null` (global) — no sprint scoping.
- `RunAgentExecutor` passes `worktreePath` + `projectId` in context — the seam for sprint context.
- Scheduler pattern to mirror: `ScheduledGroomer` / `DesignerScheduler` (5-min tick, fire-and-forget, registered in `Program.cs` ~line 1332).

## P1 — Sprint flow (the fundamental piece)

### 1a. `Orchestrator/Sprint/SprintAssembler.cs` (new hosted scheduler, 5-min tick)

Each tick, per project bundle:
1. **Completion check**: if an Active sprint exists, load member ids (`GetIssueIdsAsync`) → issues; the sprint is complete when every linked non-container issue (tasks; containers excluded) is terminal (`Completed|Failed|Closed`). If complete → `UpdateAsync` status Completed + publish `DashboardEvent` (new kind `SprintCompleted`) → fall through to assembly.
2. **Assembly** (only when no Active sprint): gather eligible tasks = `Pending` + type not in {`epic`, `story`, `pr-watch`} + not already linked to any sprint. Group by root epic (walk `ParentIssueId` chain task→story→spec→epic). Pick the group whose epic is oldest (FIFO); create sprint (`Name` = epic title, `Goal` = epic description, 14-day window, `Status: Active`), `AddIssueAsync` each task **and its parent story** (stories enable progress display; completion counts tasks only). Tasks with no epic ancestor go into an "Ad-hoc work" group (single-item sprints allowed — ALL work is sprint work). Publish `SprintStarted` event.
3. Guard: assembly is idempotent per tick; concurrent ticks across projects are per-bundle (same as ScheduledGroomer).

### 1b. Dispatch gating (`Orchestrator/Workflow` path in `OrchestratorAgent.cs`)

- No Active sprint → **no dev-task dispatch** (log at Debug, skip; watches still sweep — they're lifecycle, not sprint work). Today null sprintId = ungated; make the gate explicit: `if (activeSprint is null) skip dev dispatch`.
- `StartupRecovery` unchanged (in-flight grandfathered items requeue regardless of sprint membership).
- Legacy in-flight items (task-8/task-10, pre-sprint-model) complete via their watches; the first sprint assembles from the *next* groomed spec (nothing eligible exists post-cleanup — correct).

### 1c. Sprint-scoped shared memory

- `MemoryExtractor`: when persisting outcomes for a task that belongs to a sprint, write keys as `sprint:<sprintId>/<issueId>` (in addition to the existing issue-namespaced keys). Sprint membership: look up active sprint's `GetIssueIdsAsync` at extraction time (extractor call site is post-PR in `CommitPushPrExecutor`; pass sprintId through or resolve via `ISprintStore`).
- `MafAgentRunner`: `BuildMemoryInstructionsAsync` takes an optional `sprintId` from context; recalls `sprint:<sprintId>/` prefix into a `## Sprint memory` block + global keys into `## Project memory`.
- `RunAgentExecutor`: resolves the issue's sprint (active sprint membership) and adds `sprintId`, `sprintGoal`, `sprintSiblings` (id+title+status of sibling tasks) to context. `MafAgentRunner` prepends a `## Sprint` block (goal + sibling roster) to instructions — the cheap high-value shared context.

### 1d. Sprints page (read-only, per correction — NO propose button)

- `/api/state` sprints projection: add per-sprint `issueCount` + `doneCount` (via `GetIssueIdsAsync` + issue statuses; N is tiny).
- `Sprints.razor`: active sprint card shows goal + progress (X/Y tasks) + member list w/ status pills; past sprints collapsed below. Empty state copy: "Sprints assemble automatically when groomed work is eligible."

## P2 — Spec drill-down (from prior plan, still valid)

- API: `GET /api/specs/{id}/tree` → spec view + stories[] (grouped by `ParentIssueId`) with nested tasks (`prNumber`/`branch` from metadata) + groom runs from `IssueGroomerRunStore`.
- UI: new `SpecDetail.razor` `@page "/specs/{SpecId}"` — header, read-only body, action bar from `/api/specs/{id}/actions` (Approve / Groom / force-regroom on 409 / **Ship** via PATCH set_status), children tree with status pills + PR links (`{repoUrl}/pull/{n}` from the project record, not hardcoded), groom-run timeline.
- `Specs.razor` rows clickable → detail.

## P3 — Backlog clarity

- `/api/state` task projection: add `parentIssueId`.
- `Backlog.razor`: filters `Open (default) | Pending | InProgress | Completed | Failed | Closed | All` (Open = Pending+InProgress+Blocked — hides the 183 mass-closed dups); Parent column; PR link from `parameters.prNumber`; optional sprint badge when the task is in the active sprint (needs membership in the projection — cheap join via the active sprint's `GetIssueIdsAsync`).

## P4 — Send-to-Designer action

- Actions endpoint: add `canSendToDesign` (status == Draft; `Draft → ReadyForDesign` is already legal).
- Spec detail action bar: "Send to Designer" → PATCH set_status ReadyForDesign. `DesignerScheduler` (already registered, 5-min tick) picks it up automatically — feeds the design board.

## Out of scope

- SprintProposeService UI/operator flow (kept for future; assembler is the automatic path).
- Epic auto-close on children completion (epic-6 closes manually after spec ships).
- Sprint velocity metrics, burndown, multi-active-sprints (strictly one active sprint per project).

## Verification

- xUnit + hand-rolled fakes (no Moq), mirror `OrchestratorAgentTests` / `SpecDashboardTests` patterns:
  - `SprintAssemblerTests`: assembles one sprint from eligible epic-grouped tasks when none active; no-op while active; completes sprint when members terminal → assembles next; ad-hoc parentless task gets its own sprint; containers/watches never ingested; idempotent re-tick.
  - Orchestrator gating: no active sprint → dev task stays Pending; task in active sprint dispatches; watch still sweeps.
  - Memory: extractor writes `sprint:<id>/` keys; runner recalls prefixed block only when sprintId in context.
  - Tree endpoint: grouped 200 + 404 (existing `SpecGroomerEndpointTests` host pattern).
- `dotnet build Forge.sln` + full `dotnet test Forge.sln`; commit + push per P-item; watch CI.
- Live: enqueue a small feature through intake → approve → groom → observe sprint-1 auto-assemble (event + Sprints page), dispatch only after activation, sprint-scoped memory visible in `<dataRoot>/logs/agent.log`, sprint completes → next assembles.

## Files

- New: `Orchestrator/Sprint/SprintAssembler.cs`, `tests/Forge.Tests/Integration/SprintAssemblerTests.cs`, `Forge.UI/Components/Pages/SpecDetail.razor`
- `Orchestrator/OrchestratorAgent.cs` (gate), `Orchestrator/Workflow/RunAgentExecutor.cs` (sprint context), `Agents/MafAgentRunner.cs` (sprint block + prefixed recall), `Orchestrator/MemoryExtractor.cs` (sprint keys), `Program.cs` (register assembler)
- `Dashboard/DashboardHost.cs` (sprint rollups + parentIssueId), `Dashboard/SpecEndpoints.cs` (tree + canSendToDesign), `Dashboard/DashboardEvent.cs` (SprintStarted/Completed kinds)
- `Forge.UI/Components/Pages/Sprints.razor`, `Specs.razor`, `Backlog.razor`
- `AGENTS.md` (sprint flow paragraph under Project model), `.kilo/skills/forge-task-lifecycle/SKILL.md` (dispatch now sprint-gated)
