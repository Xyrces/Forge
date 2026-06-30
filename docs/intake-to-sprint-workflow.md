# Intake -> Sprint workflow

> Status: **DRAFT for review.** Nothing in this document is implemented
> yet. Sections marked `OPEN` need a decision before we build. Sections
> marked `PROPOSED` are my recommendation but yours to overrule.

Companion to `agent-framework-design.md`, which covers the agent
runtime itself. This document covers **what the agents do together**
from the moment an operator opens a conversation through the moment
the next sprint kicks off.

The companion document closes Phase 0 (MAF scaffolding), Phase 1
(skills, intake, specs), and the kilo removal. This document is the
forward-looking design for **Phases 2-4**: the multi-agent workflow
that turns operator intent into shipped increments.

---

## 1. Scope

**In scope:**
- The end-to-end flow from "operator has an idea" to "the next sprint
  has tasks and is about to start."
- The data model changes needed: master spec / child spec hierarchy,
  approval states, grooming traces, sprint-derivation links.
- The agent roster and their responsibilities.
- The dashboard surfaces that make the flow inspectable and
  steerable.
- Race conditions, idempotency, and human-in-the-loop semantics.

**Out of scope:**
- The MAF runtime itself (see `agent-framework-design.md`).
- The intra-engineering-loop mechanical work (worktree creation,
  commit, push, PR). That's P2.
- Long-term durability and crash recovery (that's P4, DurableTask).
  We assume single-process for Phases 2-3.

**The principle:** the operator (the human) is the **product owner**.
The agents are the **scrum team**. The dashboard is the **PM tool**.
We are building the automation of a small but real product team
where the human owns the *what* and the agents do the *how*.

---

## 2. Actors

| Role | Human/AI | Owns | Example concerns |
|---|---|---|---|
| **Operator** | Human | Product intent. Accepts epics. Approves master specs. Steers sprint scope each cycle. | "we need to support dark mode", "this epic is too big, split it" |
| **IntakeAgent** | AI (MAF) | Conversational intake of a new feature idea into a structured proposal. | "ask the operator clarifying questions until the spec is unambiguous" |
| **ProductAgent** | AI (MAF) | Once an intake is approved, expand a single epic into a full spec (acceptance criteria, scope, non-goals, open questions). | "here's exactly what success looks like; here's what we're NOT doing" |
| **GroomerAgent** | AI (MAF) | Decompose approved specs into stories + tasks sized for the engineering loop. | "a story is 1-3 tasks; each task has a clear Done" |
| **ScrumMasterAgent** | AI (rule-based, not LLM-driven) | The sprint cycle: theme/goal selection, card count, sprint kick-off. | "given the backlog, what should the next sprint be?" |
| **OrchestratorAgent** | AI (existing) | Engineering dispatch. Owns the worktree/commit/push/PR pipeline. | "claim the next Pending task, run CoreDev, open PR" |

For Phases 2-3, the **ScrumMasterAgent** is a deterministic rules
engine (a single `ScrumMaster` class that scores backlog candidates
by priority + age + dependency order). It is NOT an LLM agent and
deliberately does not have agency. The LLM agent that proposes
sprint scope is deferred — see OPEN question Q3 below.

For Phase 4, all five agents run inside a DurableTask orchestration
so a single agent crash doesn't lose the operator's work. We do not
solve that here.

---

## 3. The four phases of work

There are four distinct phases that look like a waterfall but actually
cycle:

```
   INTAKE                  PRODUCT                GROOMING                  SPRINT CYCLE
┌──────────────┐         ┌──────────────┐         ┌──────────────┐         ┌──────────────┐
│ IntakeAgent  │────────>│ ProductAgent │────────>│ GroomerAgent │────────>│ ScrumMaster  │
│              │         │              │         │              │         │              │
│ Per-feature  │         │ Per-epic     │         │ Per-spec     │         │ Per-cycle    │
│ Conversational│        │ Authoring    │         │ Decomposition│        │ (theme+goal) │
└──────────────┘         └──────────────┘         └──────────────┘         └──────────────┘
  output: proposal      output: approved spec     output: stories+tasks    output: active sprint
```

A single operator session typically produces multiple intake
proposals, which becomes multiple specs, which become multiple
sprints-worth of stories. The phases share state via the database.

---

## 4. Intake session lifecycle

### 4.1 What's an intake session?

Already implemented in P1.4 (`IntakeStore`, schema v3). A session is
a per-project conversation between the operator and the
`IntakeAgent`. The agent has project skills loaded via the
`SqliteSkillSource` (P1), has access to the codebase + in-progress
work via a new `IProjectContextSource` (Phase 2, NEW), and proposes
epics via the `create_epic` AIFunction.

### 4.2 Project context source (NEW, Phase 2)

The intake agent's job in the new world requires "total knowledge of
the codebase + in-progress work." We add a single new abstraction:

```
interface IProjectContextSource
{
    Task<ProjectContext> BuildAsync(string projectId, CancellationToken ct);
}

record ProjectContext(
    string ProjectId,
    string RepoRoot,
    IReadOnlyList<CodeSnippet> CodeSnippets,    // curated: README, key entry points
    IReadOnlyList<IssueRecord> OpenIssues,      // status != Completed AND != Archived
    IReadOnlyList<SpecRecord> RecentSpecs,      // updated within last N days
    IReadOnlyList<SkillRecord> ProjectSkills);
```

**Open / to decide:** how `CodeSnippets` are populated. PROPOSED:
on first intake per project, walk the repo and grab `README.md`,
the top-level `*.sln`, and one representative file per top-level
directory. Cache on disk. Re-walk on demand (operator click
"refresh project context"). We are NOT doing RAG embeddings yet —
this is a hand-curated snapshot, fast and explainable.

### 4.3 The chat

The chat is already implemented. The intake agent is told:
- "You are the IntakeAgent for project {projectId}. The codebase and
  open issues are described below. Help the operator refine their
  idea into a series of epics. Use create_epic when an idea is
  shaped enough; ask questions when it isn't."

The chat session persists in `intake_session` + `intake_message`.
The agent's "memory" of the conversation is the persisted history.

### 4.4 Multi-epic proposal

**PROPOSED**: the current `create_epic` AIFunction is unchanged at
the action level, but the operator now thinks of an intake session
as producing *N* epics, not one. The intake flow:
1. Operator says "we need to support dark mode."
2. Agent asks clarifying questions (sessions, themes, persistence,
   system preferences vs in-app toggle, etc.).
3. Agent calls `create_epic("Dark mode: settings persistence",
   "Persist the user's theme choice across sessions and devices",
   priority=2)` for the data layer piece.
4. Agent calls `create_epic("Dark mode: theme variables",
   "Define a token set for surfaces (panel, accent, fg, muted) and
   wire them through the design system", priority=2)` for the
   styling piece.
5. Agent calls `create_epic("Dark mode: in-app toggle",
   "Add an in-app theme switcher with a system-preference follow option",
   priority=3)` for the UI piece.
6. Each call produces a system message in the session linking
   back to the proposed issue id.

The operator sees three Accept buttons in the chat (one per
proposed epic), plus an "Accept all" affordance. The dashboard
intake tab becomes denser.

### 4.5 Spec-first vs issue-first for proposal output

`Open / PROPOSED:` Should the agent create a `spec` (master) FIRST
and reference it from each `create_epic`, or should the agent
continue to create bare epics and the product authoring happens in
a separate phase?

**Recommendation: spec-first.** Two reasons:
- The agent's job is to be authoritative about *what we're going
  to ship*. An issue alone ("fix the auth flow") is too thin.
- The spec becomes the durable record of intent, durable across
  grooming, durable across re-orgs.

Refined flow for the dark-mode example:
1. Agent creates `spec` "Dark mode" — master with current
   discussion summary, scope, non-goals, open questions. Status
   `Draft`. `parent_spec_id = NULL`.
2. Agent creates `spec` "Dark mode: settings persistence" — child,
   `parent_spec_id = <master>`. Status `Draft`. Calls
   `propose_epic(parent_spec_id, title, body)` which creates the
   epic AND records `parent_issue_id` for the spec.
3. Agent repeats for each child.

The Accept button on a *child* epic in the chat now accepts a
child spec + epic as a unit. The master spec stays in `Draft`
until ALL children have been accepted (see section 5).

> **OPEN Q1:** Is the master-spec gating the right shape, or
> should the operator accept each child independently with no
> master-approval gate? I lean master-gated because it forces
> alignment before grooming starts.

---

## 5. Master + child spec authoring

### 5.1 Spec tree

```
spec (master, parent_spec_id = NULL)
├── spec (child, parent_spec_id = master)
├── spec (child, parent_spec_id = master)
└── spec (child, parent_spec_id = master)
```

Schema-wise, this is already supported: `spec.parent_spec_id`
exists in v4 (added in P1.5.a). What changes is the **status
contract** of the master.

### 5.2 Master status lifecycle

```
Draft ────────> Proposed ────────> Approved ─────────> Grooming ─────────> Shipped
  │                │                 │                    │                │
  │                │                 │                    │                └─ all child
  │                │                 │                    │                   epics Completed
  │                │                 │                    └─ GroomerAgent decomposes
  │                │                 │                       each Approved child spec
  │                │                 │                       into stories + tasks
  │                │                 │
  │                │                 └─ operator clicks
  │                │                    Approve (or Reject-
  │                │                    with-feedback loops back
  │                │                    to Draft)
  │                │
  │                └─ operator clicks "Ready for review" on
  │                   each child spec; once all children are
  │                   non-Draft, the master auto-transitions
  │                   to Proposed
  │
  └─ intake session still active, agent proposing children
```

Master transitions to `Proposed` when:
- All child specs have been visited by the product agent
  (`spec.body IS NOT NULL AND spec.author LIKE 'product:%'`), AND
- All child specs are in `{Approved, Superseded}` (i.e. none is
  still `Draft`).

Master transitions to `Approved` when the operator clicks
"Approve master spec" in the dashboard. There's no path
`Proposed -> Approved` other than the operator clicking the
button.

> **OPEN Q2:** Should we require operator approval of the master,
> or auto-approve when all children are Approved? I lean
> approval-required because the master is the durable record and
> the operator is the product owner. But this is friction.

### 5.3 Child spec authoring (Phase 2-3)

When the operator clicks Accept on a child epic:
1. The linked child spec moves from `Draft` to `Approved`
   (operator is implicitly approving the spec body).
2. The GroomerAgent wakes up (or is queued) for that spec.
3. The ProductAgent also runs once per child to expand the
   accept criteria + scope + non-goals + open questions.
   The IntakeAgent's `create_epic` call already created a
   one-paragraph body for the spec. The ProductAgent rewrites
   that into the structured form (see 5.4 below).
4. Once ProductAgent has authored the child spec, the spec
   moves to status `Draft` (yes, the operator goes Draft ->
   operator-approved-as-part-of-intake -> Draft again as product
   refines it, then Draft -> Approved by operator).

> **OPEN Q2.5:** Is this status churn too much? Alternatives:
> (a) skip the operator approve on intake; product refines
> directly to Draft and operator approves once at the master level;
> (b) keep intake-body, have product append a "refined scope"
> section instead of rewriting.

I lean (a). The operator's "Accept" button is then strictly about
"yes we will work on this epic" (an issue-level decision), not
about spec content. The spec gets one review at the master level.

### 5.4 Spec structure (default + override)

The product agent writes specs in this shape (markdown):

```
## Summary
One-paragraph restatement of the epic in operator's own words.

## Acceptance criteria
- [ ] concrete, testable behavior...
- [ ] concrete, testable behavior...

## Out of scope
- explicit non-goal...
- explicit non-goal...

## Open questions
- question still to answer?...
- question still to answer?...

## Notes
(optional) technical notes, dependencies on other specs, etc.
```

The Specs tab renders this with a section-style layout:
"Summary" + "Acceptance criteria" + "Out of scope" + "Open
questions" + "Notes" each in their own card. If a section is
missing, it's not rendered (freeform override). Plain `body`
without section headings is rendered as a single code block,
preserving formatting.

Freeform override means: the agent is *encouraged* to use the
template but isn't required. The agent's instructions say "if you
have a good reason to skip a section (e.g. an exploratory spec
with only Open Questions), skip it." Operator-facing reads stay
uncluttered.

### 5.5 Versions

Already implemented in P1.5.a: every spec body update appends a
new `spec_version` and bumps `spec.current_version`. The
Specs tab shows the version history with author + timestamp.

PROPOSED: when the product agent refines a spec, author is
`"product:<run_id>"` so the operator can distinguish their own
edits from agent edits in the version history.

---

## 6. Grooming phase

Once a child spec is `Approved`, the GroomerAgent runs.

### 6.1 Goals

- Decompose the spec into 1-3 stories.
- Each story becomes 1-3 engineering tasks.
- Tasks are sized so the engineering loop (worktree, edit, test,
  commit, push, PR) can complete in a single CoreDev/ClientDev
  agent run (P0 showed the agent can do this — the no-op commit
  branch in P0 means "agent ran but didn't edit any files," not
  a failure).
- Acceptance criteria from the spec become task titles or
  descriptions.

### 6.2 Output

```
spec (Approved) ── grooming ──> stories + tasks (in IssueStore)
                                  ├── issue (type=story)
                                  │   ├── task (Acceptance: ...)
                                  │   ├── task (Acceptance: ...)
                                  │   └── task (Acceptance: ...)
                                  ├── issue (type=story)
                                  │   └── task x2
                                  └── issue (type=task, optional)
                                      └── (e.g. cross-cutting infra)
```

Each story has `parent_id = spec.id`. Each task has `parent_id =
story.id`. Issues already have a `parent_id` column (added in
the design back in P0). The hierarchy is `spec -> story -> task`
via `parent_id`.

### 6.3 Story/task metadata

Issue `type` is enriched:
- `type = "story"` for stories.
- `type = "task"` for tasks (default).
- `type = "epic"` stays as the issue-level epic from intake.
- `type = "spec"` is reserved for cross-references but the spec
  itself lives in the spec table, not the issue table.

### 6.4 Acceptance criteria mapping

The spec's `## Acceptance criteria` checkboxes become the task
titles in the order they appear. Example:

```
Spec: "Dark mode: in-app toggle"
## Acceptance criteria
- [ ] "System" option follows OS preference
- [ ] Choice persists across page navigation
- [ ] Theme switch animates over 200ms

Becomes:
Issue type=story, title "Dark mode: in-app toggle"
  metadata.parent_spec_id = spec.id
  ├── task "System option follows OS preference"
  ├── task "Choice persists across page navigation"
  └── task "Theme switch animates over 200ms"
```

> **OPEN Q3:** What if the spec has 12 acceptance criteria and the
> story should be 3 tasks? PROPOSED: split into 3 stories with 4
> criteria each, then 1 task per story. Stories preserve the
> "epic-size" meaning. Tasks are 1:1 with criteria, bounded 1-3.

### 6.5 GroomingAgent prompt shape

```
You are the GroomerAgent for project {projectId}. Given the
following Approved spec, decompose it into 1-3 stories of 1-3
tasks each. Each task must be completable in a single
engineering agent run.

Spec:
{body}

Available tools:
- create_story(title, parent_spec_id, acceptance_criteria):
  creates a story issue linked to the spec. Returns the
  story issue id.
- create_task(title, parent_story_id, parent_spec_id): creates
  a task linked to the story. Returns the task issue id.

After creating the stories and tasks, call set_spec_status(
spec_id, "Grooming") to mark the spec as decomposed. The
system will move it to Shipped when all of its tasks are
Completed.
```

This is a one-shot agent run. The agent decides the decomposition.
No iterative back-and-forth (the operator can intervene manually
in the Tasks tab if the decomposition is wrong).

---

## 7. Sprint cycle (the scrum loop)

This is the part that is least defined. I have a proposal, you have
opinions, let's make sure we agree before coding.

### 7.1 The cycle

After grooming, the backlog has Approved/Grooming specs with
Pending tasks. The cycle is:

```
        selected sprint scope
              │
              ▼
   ┌────────────────────────┐
   │ Sprint N starts        │
   │ kick off pending tasks │
   └────────────┬───────────┘
                │
                ▼
       Engineering runs
       tasks complete
                │
                ▼
    All tasks done / sprint
    ends / operator reviews
                │
                ▼
   ┌────────────────────────┐
   │ NEXT SPRINT            │──────> back to top
   │ Operator picks:        │
   │ - theme                │
   │ - goal                 │
   │ - N tasks (default 5)  │
   └────────────────────────┘
```

A "sprint" already exists in the data model as `sprint` +
`sprint_issue` (P0). What is NEW is the **selection ritual**.

### 7.2 Sprint selection (the scrum loop)

The operator opens the Sprints tab and clicks "Plan next sprint".
A modal asks:
- **Theme** (one-line label): e.g. "Auth polish"
- **Goal** (one paragraph): e.g. "Ship the claims middleware
  migration and deprecate the legacy session table"
- **N cards** (number, default 5): how many tasks to fill this
  sprint

The ScrumMasterAgent (deterministic rules, see Section 2) then
selects N pending tasks per this scoring:

```
score(task) =
    + 10 if task.priority == 1 (highest)
    + 6  if task.priority == 2
    + 3  if task.priority == 3
    + 2  if task.priority == 4
    + 1  if task.priority == 5
    + 5  if task's parent story is linked to a spec with
          matching theme substring in title or body
    + 2  per day of age (capped at +10)
    - 20 if a downstream task is already in another sprint
    +0  otherwise
```

PROPOSED: this is a starting point. PROPOSED to expose the score
in a "Why this task?" tooltip on each card in the modal so the
operator can see the reasoning and override.

> **OPEN Q4:** Is the deterministic scoring enough? My take: yes
> for Phase 3 because it's explainable and the operator can
> always add/remove cards. An LLM-driven "propose sprint scope"
> is a Phase 4 ask. If you disagree, let's add it to Phase 2.

> **OPEN Q5:** What does the dashboard show when the user picks
> "N=5 cards" but the backlog only has 3? PROPOSED: show "only
> 3 tasks available; plan 3?" with an OK button.

### 7.3 Sprint kick-off

When the operator clicks "Start sprint":
1. Set `sprint.status = "active"`, `start_date = now`,
   `end_date = now + sprint.length_days` (PROPOSED default: 14).
2. For each of the N selected tasks: dispatch to
   `OrchestratorAgent` (the existing claim -> run -> worktree
   pipeline). The orchestrator dispatches in parallel up to
   `SpawnerOptions.MaxConcurrentSessions` (default 4).
3. The dashboard moves tasks to `InProgress` as they're claimed.

### 7.4 Sprint end

When all the sprint's tasks are Completed (or the operator
manually ends the sprint):
1. Sprint moves to `status = "completed"`.
2. The Specs tab moves all `Grooming` specs whose tasks are
   now done to `Shipped`.
3. Operator can click "Plan next sprint" and the cycle repeats.

---

## 8. Data model additions

What we need to add or change to support the above:

### 8.1 Issues (existing table, additive)

| Column | Type | Default | Purpose |
|---|---|---|---|
| `parent_id` (already exists) | TEXT NULL | NULL | Generic parent. Used for spec->story->task chain. |
| `acceptance_criteria_index` | INTEGER NULL | NULL | Order within parent spec; populated by GroomerAgent. |

> The chain `spec -> story -> task` is `parent_id` only. We do
> not need new columns for the chain itself.

### 8.2 Specs (schema v4, additive)

| Column | Type | Default | Purpose |
|---|---|---|---|
| `author` (already exists) | TEXT NULL | NULL | Already set. New convention: `"product:<run_id>"` for product-authored, `"human"` or `NULL` for operator-authored. |

We do NOT add new columns. `parent_spec_id`, `current_version`,
`status` cover everything.

### 8.3 New table: sprint_selection

A new table to record the scrum-loop audit trail. Why this needs
to be its own table: the user wants to see "why was this task
included?" in the dashboard, and the answer depends on the
scoring context at sprint-plan time (which tasks existed, which
were in other sprints, etc.) — we can't reconstruct it from the
sprint_issue table alone.

```sql
CREATE TABLE sprint_selection (
    sprint_id        TEXT NOT NULL,
    task_id          TEXT NOT NULL,
    included         INTEGER NOT NULL,    -- 1 or 0
    score            INTEGER NOT NULL,
    score_breakdown  TEXT NOT NULL,       -- JSON: ["+10 priority=1", "+5 theme match", ...]
    operator_override INTEGER NOT NULL,    -- 1 if operator flipped the default
    created_at       TEXT NOT NULL,
    PRIMARY KEY (sprint_id, task_id)
);
```

> PROPOSED. The score_breakdown makes the explainability trivial
> and gives the operator a paper trail of decisions. If you don't
> care about this, we can derive it later from a log.

### 8.4 New table: intake_session_master

To make the "intake produces a master spec" link clean. Each
intake session optionally owns one master spec (or none if the
intake ended without proposing anything).

```sql
ALTER TABLE intake_session ADD COLUMN master_spec_id TEXT;
ALTER TABLE intake_session ADD COLUMN master_spec_id REFERENCES spec(id);
```

Alternatively, store this as metadata on the spec: `spec.author
= "intake:<session_id>"`. PROPOSED: use metadata (matches existing
conventions; no new schema change needed).

### 8.5 Sprint status

PROPOSED: add `status = "Planning"` to the sprint lifecycle:
`Planning -> Active -> Completed`. The current schema has
`Active` already; Planning is new.

`Planning` means: tasks selected, sprint not started yet. Lets
the operator see a half-built sprint in the Sprints tab and
adjust before clicking Start.

---

## 9. New abstractions (Phase 2 / Phase 3)

### 9.1 IProjectContextSource (Phase 2)

```
namespace PortHorizon.Agents.Agents;

public interface IProjectContextSource
{
    Task<ProjectContext> BuildAsync(string projectId, CancellationToken ct);
}

public sealed record ProjectContext(
    string ProjectId,
    string RepoRoot,
    IReadOnlyList<CodeSnippet> CodeSnippets,
    IReadOnlyList<IssueRecord> OpenIssues,
    IReadOnlyList<SpecRecord> RecentSpecs,
    IReadOnlyList<SkillRecord> ProjectSkills);
```

Implementation: `FilesystemProjectContextSource`:
1. Walk the `WorkspaceOptions.Root` directory.
2. Snapshot `README.md`, top-level `*.sln`, `*.csproj`,
   one representative `.cs` per top-level subdir.
3. Read `AgentStore`, `IssueStore` (Pending + InProgress),
   `SpecStore` (recent, by `updated_at`).
4. Read `SkillStore`.

Caching: write the snapshot to `.portHorizon/context/<project>.json`
on first build; refresh on demand.

### 9.2 ProductAgent + GroomerAgent (Phase 2)

Two new agent classes, each a `ChatClientAgent` (the MAF
abstraction) with its own tool set. Both extend the same base:

```
abstract class SpecLifecycleAgent
{
    protected ChatClientAgent NewAgent(AgentType role, string sessionId, AIFunction[] tools);
}

sealed class ProductAgent : SpecLifecycleAgent
{
    // tools: propose_spec, propose_epic, update_spec
    Task<SpecRecord> AuthorChildSpec(string specId, CancellationToken ct);
}

sealed class GroomerAgent : SpecLifecycleAgent
{
    // tools: create_story, create_task, set_spec_status
    Task GroomAsync(string specId, CancellationToken ct);
}
```

### 9.3 ScrumMaster (Phase 3, deterministic)

NOT an LLM agent. A pure C# class:

```
sealed class ScrumMaster
{
    public SprintPlan Propose(IReadOnlyList<IssueRecord> backlog,
                              SprintSelectionCriteria criteria);
}

sealed record SprintPlan(SprintRecord Sprint,
                          IReadOnlyList<TaskCandidate> Selected);

sealed record TaskCandidate(IssueRecord Task, int Score,
                            IReadOnlyList<string> ScoreBreakdown);
```

### 9.4 Dashboard additions

- **Specs tab** (P1.5.a, done): read-only list + detail.
- **Specs tab** (P2): add "Approve master" + "Start grooming"
  buttons (operator actions). Also show `parent_spec_id` as a
  breadcrumb.
- **Tasks tab**: show spec/story chain on hover; show
  `parent_id` chain in the row tooltip.
- **Sprints tab**: add "Plan next sprint" button (P3).
- **Intake tab** refinement (P2): SSE streaming output so the
  operator sees tokens land as the agent produces them; per-message
  Accept for child epics; "Accept all proposed epics" button.

---

## 10. Race conditions, idempotency, rollback

Things that can go wrong, and what we do:

### 10.1 Operator closes browser mid-intake

The intake session + message history is in SQLite, durable. On
reopen, the next message resumes the conversation with the
existing history. No special handling.

### 10.2 Intake agent crash mid-proposal

A `create_epic` call either succeeds (issue row written) or
fails (no row). The session messages are persisted before the
LLM call (P1.4 pattern). If the LLM call throws, the session is
left in a recoverable state — operator can resend.

### 10.3 Operator clicks Accept on a child epic twice

The Accept endpoint is idempotent: it's an `insert or ignore` on
the `sprint_issue` table (existing behavior of SprintStore). The
dashboard hides the Accept button after first click.

### 10.4 Product agent writes a spec while operator edits it

Last-writer-wins. We do not currently implement optimistic
locking on specs. If this becomes a problem we add an
`updated_at` precondition on PATCH; for P2 PROPOSED to skip it.

### 10.5 Operator changes a task's spec link after grooming

PROPOSED: `parent_id` is set during grooming and never edited
after. If the operator wants to re-link, they manually create a
new task. We do NOT support task -> spec re-linking in P2-3.

### 10.6 Sprint ends with some tasks still Pending

PROPOSED: the sprint's `end_date` triggers a "sprint ended" event
in the dashboard. The operator decides:
- "Move incomplete tasks to next sprint" (re-score).
- "Mark incomplete tasks as Failed" (cancel).
- "Extend the sprint by N days" (rare).

We surface this as a Sprints-tab banner; we don't take an
automatic action.

---

## 11. What we ship in each phase

### Phase 2 (next)
- `IProjectContextSource` + `FilesystemProjectContextSource`.
- `ProductAgent` with `propose_spec` / `propose_epic` /
  `update_spec` tools.
- Intake tab refinement: SSE streaming output, per-message
  Accept-all, accept child-epic-as-unit.
- Refined `create_epic` flow that produces a child spec alongside
  the issue.
- Master-spec gating logic: "Proposed when all children authored
  + Approved-or-Superseded."
- Tests for the above.

### Phase 3
- `GroomerAgent` with `create_story` / `create_task` tools.
- `ScrumMaster` deterministic scorer.
- Sprints tab "Plan next sprint" modal.
- `sprint_selection` audit table.
- Tests for the above.

### Phase 4
- DurableTask for crash recovery.
- Multi-process scaling (probably not — single process is the
  contract).
- Engine handoff (P3.5 in the original design doc) becomes "inbox"
  of pre-groomed stories a developer can pull.

---

## 12. Open questions (must answer before code)

Listed by section so we can resolve them in order:

- **Q1** (4.5): Spec-first vs issue-first proposal output. **My
  pick: spec-first.**
- **Q2** (5.2): Operator approval of master required, or
  auto-approve? **My pick: required.**
- **Q2.5** (5.3): Status churn during product refinement. **My
  pick: simplify — operator Accept only approves the epic, not
  the spec; product refines directly to Draft.**
- **Q3** (6.4): How to split >3 acceptance criteria. **My pick:
  multiple stories, 1 task per criterion.**
- **Q4** (7.2): Deterministic scorer sufficient, or LLM-driven?
  **My pick: deterministic for P3, LLM-driven for P4.**
- **Q5** (7.2): Backlog smaller than N. **My pick: confirm with
  operator, "plan N-1?"**
- **Q6** (8.3): Per-task sprint score breakdown as a separate
  table? **My pick: yes, makes explainability free.**
- **Q7** (8.5): Sprint `Planning` state before `Active`? **My
  pick: yes.**
- **Q8** (10.4): Optimistic locking on spec edits? **My pick:
  skip for P2, add if it actually becomes a problem.**

---

## 13. What this document does NOT decide

For follow-up docs as we build:
- **Theme taxonomy / tagging**: even with the deterministic
  scorer, we may want to tag issues with a theme (e.g.
  "auth", "ui", "infra") for analytics. Defer until we have
  data.
- **Multi-project** intake: should a single operator session
  span multiple projects? Today: no. Defer.
- **Agent memory across runs**: the IntakeAgent's context is
  reconstructed from the session history + project context on
  each run. There is no agent-to-agent memory. Phase 4 ask.
- **Evaluation harness**: how do we know the ProductAgent
  wrote a good spec? Do we unit-test with mock LLMs (like we
  do today) or do we eventually need a few real LLM evals with
  the operator scoring them? Defer.
