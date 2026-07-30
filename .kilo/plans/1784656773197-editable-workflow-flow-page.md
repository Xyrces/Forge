# Editable workflow on the Flow page (Zapier-style, pass-based)

## Goal

Turn the Flow page from a read-only renderer of a hardcoded DAG into the operator's
workflow control surface: the current pipeline stays byte-for-byte as the built-in
default, but becomes editable and maintainable as a workflow definition — edited in a
Zapier-style vertical step editor, draft-then-publish, with restorable snapshots.

## Decisions (locked with operator)

1. **Edit semantics: wiring & policy edits only.** The workflow definition controls
   what existing machinery already knows how to honor: enable/disable of *optional*
   steps, stage-gate placement + hold/release, run-gate lists, and policy values
   (max strikes, stall grace, park-on-infra, auto-merge vs hold, branch behavior from
   predefined options). The `TaskStateMachine` transition table and event reporters
   stay code-owned. Users can never author a state/transition the code can't trigger.
2. **Four passes, increasing granularity:** render → gates → policies → structure.
   Each pass independently shippable and behavior-safe.
3. **Draft + Publish** (not live-immediate): edits accumulate in a draft; Publish
   validates and overwrites live. Diverges deliberately from the run-gates
   live-immediate pattern because structural edits have bigger blast radius.
4. **Page topology: Live / Edit mode toggle** on `/flow`. Live = today's DAG, counts,
   journey trace (untouched). Edit = the Zapier-style editor.
5. **Publish keeps version snapshots** (last ~10, memory-keyed). Restore = publish an
   old snapshot.

## Current state (what's hardcoded where)

- Flow graph: `Dashboard/Flow/FlowGraph.cs` — static `Nodes`/`Edges`, fixed X/Y,
  `ClassifySpec`/`ClassifyIssue`, `BuildJourney`. Rendered by
  `Forge.UI/Components/Pages/Flow.razor` from `GET /api/flow`
  (`Dashboard/FlowEndpoints.cs`).
- Implementation lane semantics: `Core/TaskStateMachine.cs::BuildTable` (events ×
  states) — code-owned, stays code-owned.
- Planning lane semantics: `SpecStatus` machine + `ScheduledGroomer` + designer
  dispatch + `SprintAssembler` — code-owned, stays code-owned.
- Stage gates: `Core/StageGates.cs` — 4 fixed stages, memory keys `gate/<stage>`.
- Run gates: `Agents/Gates/` — already editable (DB override → config → default);
  the resolution-pattern precedent for this whole feature.

## Definition model (Pass 1 deliverable, Core-owned)

`Core/Workflow/WorkflowDefinition.cs` — JSON-serializable:

- `Steps[]`: `{ id, label, lane (planning|implementation), kind, optional (bool),
  enabled (bool), policies { key: value }, gates [stage-gate names attached here],
  x, y (Live-view layout; optional with auto-layout fallback) }`.
- `Edges[]`: `{ from, to, kind (happy|branch|loop|failure), label, condition }`.
  Edges are descriptive (they drive rendering + gate placement), not executable.
- Built-in default: `WorkflowDefaults.Definition` reproduces today's `FlowGraph`
  nodes/edges/coordinates exactly, plus current policy values as properties
  (`maxStrikes: 3`, `stallGraceMinutes: 35`, `parkOnInfra: true`, `autoMerge: true`,
  `noDiffOutcome: completed`, design fast-path edge present).

**Storage & resolution** (mirrors stage gates / run gates; per-project MemoryStore,
effectively global until multi-project dispatch lands):

- `workflow/live` — published override (absent = built-in default).
- `workflow/draft` — the editor's working copy.
- `workflow/versions/<utcTicks>` — snapshot of previous live, written on each
  Publish; pruned to newest 10.
- Resolution per read: `workflow/live` → built-in default. No restart. No new tables
  (35GB-cap rule; definitions are small, snapshots bounded).

**Publish validation (fail closed, operator-readable errors):** schema-valid JSON;
all step ids known to the catalog; policy keys known + value ranges checked;
non-optional steps still enabled; every attached gate name known
(`StageGates.IsKnown` or registered human-approval gate, future).

**Publish semantics for in-flight work:** policies/gates resolve per evaluation —
the next PRWatcher sweep / scheduler tick picks up the new live definition. No
migration, no restart, no rewind of in-flight tasks. Publish/restore emit a
`DashboardEvent` (audit on the Events tab).

## Pass 1 — Canonical definition + render-from-it (no behavior change)

1. `Core/Workflow/WorkflowDefinition.cs` + `WorkflowDefaults` (today's graph +
   policies as data) + `WorkflowResolver` (MemoryStore `workflow/live` → default).
2. `Dashboard/Flow/FlowGraph.cs` stops being the source of truth: `/api/flow`
   (`FlowEndpoints.cs`) serves nodes/edges from the resolved definition. Keep
   `ClassifySpec`/`ClassifyIssue`/`BuildJourney` logic as-is (maps reality onto
   step ids).
3. Flow page: no visible change. Verify pixel/layout parity with today's page.
4. Tests: default definition round-trips and equals today's static graph (node ids,
   edges, coordinates); `/api/flow` output unchanged; resolver falls back to default
   on absent/corrupt `workflow/live`.

## Pass 2 — Zapier-style Edit mode + gate control

1. API (`Dashboard/WorkflowEndpoints.cs`): `GET /api/workflow` (resolved live +
   draft + diff summary), `GET /api/workflow/default`, `PUT /api/workflow/draft`,
   `DELETE /api/workflow/draft` (discard), `POST /api/workflow/publish` (validate →
   snapshot → overwrite live → event), `GET /api/workflow/versions`,
   `POST /api/workflow/versions/{id}/restore`.
2. UI: Live/Edit toggle in the Flow page header. Edit mode = vertical numbered step
   cards with `+` connectors (Zapier layout from the reference screenshot):
   - Main spine top→bottom: intake → design → groom → backlog → sprint → dispatch →
     agent → PR → review → merge.
   - Loops/failure sinks (rework, parked, blocked) render as expandable branch chips
     on the owning card ("on CI failure → Rework loop"), Paths-style — NOT drawn
     back-edges.
   - Step cards expand to configure: stage-gate attach + hold/release at that
     transition; everything else read-only badges ("policy editing lands in pass 3").
3. Draft UX: unsaved-changes badge, diff summary vs live (added/removed/changed
   gates), Publish with confirm, Discard, version list with restore.
4. UI-consistency rule (2026-07-24: never edit the same setting in two places):
   Edit mode becomes the canonical stage-gate surface; the Sprints-page gate strip
   converts to read-only status chips linking to `/flow?mode=edit`. Run-gate editing
   stays on `/gates` — step cards cross-link, do not re-implement.
5. Tests: draft CRUD, publish validation failures (unknown step, bad range, disabled
   non-optional), snapshot pruning at 10, restore round-trip, diff computation.

## Pass 3 — Policy editing

1. Machinery reads policies from the resolved definition instead of constants:
   - `PRWatcher`: max rework attempts, rework-round grace (35m), park-on-infra
     on/off, auto-merge vs hold.
   - `Core/TaskState.cs`: `MaxStrikes`, `StallGrace`.
   - Planning lane policies deferred unless trivially readable (schedulers tick
     independently — note in code which are definition-backed).
2. UI: per-step policy panel in Edit mode (bounded inputs, unit labels, reset per
   policy to default).
3. Tests: each policy honored from an override definition; out-of-range rejected at
   publish; default definition preserves today's constants exactly.

## Pass 4 — Bounded structural edits

1. Enable/disable of `optional: true` steps only: design (intake→groom fast path
   already exists), reviewer-agent (merge then requires formal approval), artist.
   Disabling a non-optional step is rejected at publish.
2. Branch behavior from predefined options per branch edge (e.g., CI-red →
   `rework` (default) | `block`; no-diff → `completed` | `rework`). Reporters switch
   on the resolved option.
3. Classification + journey building honor disabled steps (a task can't classify
   into a disabled node; planner-lane fast paths follow enabled edges).
4. Human-approval-gate steps as addable cards: explicitly DEFERRED to the planned
   human-approval-gates epic; the editor must not pre-build it (epic-11 constraint).
5. Tests: disabled design → intake classification uses fast path; disabled
   reviewer-agent → merge requires formal approval at head; branch option switch
   changes reporter routing; validation rejects disabling non-optional steps.

## Risks / guardrails

- **Definition the engine can't execute:** prevented by decision 1 — edges are
  descriptive, transitions stay code-owned, publish validation rejects unknown
  ids/options.
- **Blast radius of a bad publish:** draft+publish, validation, snapshots, and
  reset-to-default. Policies resolve per evaluation, so a bad value can be
  re-published immediately.
- **UI drift between Live and Edit:** both render from the same resolved
  definition; Live keeps the DAG, Edit the vertical layout — pass 1 parity check is
  the guard.
- **Two-places rule:** pass 2 converts the Sprints gate strip to read-only +
  cross-link; run gates stay on `/gates`.
- **Datastore growth:** memory keys only, snapshots pruned to 10, no new tables.

## Validation (each pass)

- `dotnet build Forge.Core/Forge.Core.csproj` clean (TreatWarningsAsErrors).
- `dotnet test tests/Forge.Tests/Forge.Tests.csproj` — suite green + new tests per
  pass (listed above).
- `--check` pre-flight; deploy via publish + `systemctl --user restart forge`;
  smoke: `/api/flow` 200 + parity (pass 1), editor round-trip in browser (pass 2+).

## Open questions (out of scope unless operator says otherwise)

- Multi-project: definitions are per-project memory (precedent), effectively global
  until multi-project dispatch lands. No cross-project templates in v1.
- Planning-lane scheduler policies (groomer eligibility, sprint assembly rules):
  candidates for a pass 5, not committed here.
- Draft sharing/conflict: solo operator, last-write-wins; no locking.
