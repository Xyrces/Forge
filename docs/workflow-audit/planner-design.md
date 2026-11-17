# Bounded sprint planning design

Status: design for review, **not implemented**. Baseline is the sprint code at `8460dde` and the seven executable characterization cases in `tests/Forge.Tests/SprintPlanningAuditTests.cs`. Budget implementation is a separate authorized change.

## Operating contract

An initiative enters through operator intake. Forge may identify an opportunity, draft a proposal, ask questions, and expand that draft with alternatives, scope, acceptance criteria and dependencies. It must not turn its own suggestion into approved engineering work. Acceptance authorizes the initiative's outcome; subsequent design, grooming, bounded sprint planning, implementation, review and ordinary remediation run autonomously. The operator does not approve each sprint.

A sprint is a small, demonstrable outcome inside already approved scope. It may contain a slice of one spec, complementary tasks from other approved specs, and related remediation. An epic/spec boundary is provenance, not a mandatory sprint boundary. Membership includes the prerequisites needed to deliver the outcome; dependency readiness determines execution order within that membership.

The active sprint keeps its committed objective. Necessary prerequisites and corrective work may be added through the same validator. Unrelated work, even if high priority, does not silently displace or expand the active objective. An explicit operator interruption is a separate action with a visible disposition for unfinished work.

## Reuse the existing contracts

| Existing contract | Keep | Change |
| --- | --- | --- |
| `Agents/IntakeAgent.cs` draft messages and acceptance, `Core/IntakeStore.cs` | Session/project ownership, proposal history, operator acceptance | Persist a durable acceptance identity and approved outcome snapshot; do not rely on matching a system-message string as the authorization record |
| `SpecRecord` project, parent epic/spec, `CurrentVersion`, body | Project ownership and versioned technical decomposition | Carry the accepted initiative identity through derived specs; technical refinement does not itself require another approval |
| Task parent chain, `followUpOf`, `blocks`, grooming | Provenance, prerequisite graph, technical readiness | Add structured outcome/acceptance references during grooming; a title match or provenance root alone is not a coherence test |
| `SprintProposalAuditStore` candidate/selection audit and committed sprint | One proposal/audit identity, explanatory selection | Extend it with typed plan JSON, input revision/fingerprint and validation result; avoid a second competing planner database |
| `SprintStore`, memberships and status hints | Per-project sprint, tasks and visible goal | Add transactional validated activation and membership amendment; no automatic archiving of another active sprint |
| `FollowUpDraftStore` source, role, disposition | Drafts and discovery history | Add stable materialization identities and restart-safe create/consume |
| `IssueStore.ReadyAsync` and role slots | Dependency-ready dispatch within active membership | Do not use runnable-now status to decide whether a prerequisite belongs in the plan |

Current approval records are not strong enough to treat every Groomed spec as proof of operator authorization. In particular, `SprintAssembler.MaterializeFollowUpsAsync` currently handles triage action `epic` by creating a spec and immediately setting Approved. Replace that path: genuine new initiatives become intake proposal drafts; in-scope remediation retains its accepted initiative reference and goes through normal technical grooming. This closes an authorization gap rather than adding another planning approval gate.

## Proposed plan representation

Extend the existing proposal audit with a versioned `SprintPlan` payload:

- Project, accepted initiative references, source spec versions and input snapshot fingerprint.
- One objective and a short list of acceptance criteria, each linked to approved scope and required evidence.
- Selected engineering task IDs, their criterion mapping, mandatory prerequisite IDs, and inclusion reasons (`delivers`, `prerequisite`, `remediates`).
- Related candidates deferred from this sprint, with reasons. Deferral is not closure or abandonment.
- Planning bounds, known role requirements, and any unestimated effort/cost uncertainty.
- Validation result, blockers, activation identity and resulting sprint ID.

Use typed fields rather than another set of task lifecycle flags. Issue lifecycle remains owned by `TaskStateMachine`. Structured planner data is a planning/audit contract, not a replacement state machine. Any schema changes receive their own migration after the current budget migration; do not assign or reuse M039 here.

The initial selector should be deterministic. Reuse grooming to describe bounded delivery slices and criterion links. Rank approved candidate outcomes by existing operator priority and age, then collect their related tasks and prerequisite closure. A later capable-model suggestion may help compare existing outcome candidates, but it cannot create approval, change the backlog, activate a sprint, or bypass validation. This does not require a new general-purpose planning agent.

## Selection and activation

1. **Snapshot the owning project's work.** Include approved provenance, groomed tasks, dependencies, existing sprint membership, current task/spec revisions, and relevant remediation. Exclude containers from executable membership. Treat a task with missing or ambiguous authorization as a proposal/grooming problem, not implicitly approved work.
2. **Choose a candidate outcome.** Use the smallest groomed slice with independently demonstrable acceptance. Search related approved backlog across spec boundaries before committing it. Shared files, matching titles, or available worker slots are hints, not sufficient inclusion reasons.
3. **Close over prerequisites.** A -> B -> C may all belong to one sprint while only A is runnable. A dependency already delivered is satisfied. An open prerequisite joins the candidate only if it is approved, groomed, owned by this project and not committed to another active sprint. Cycles, missing nodes, failed prerequisites or cross-project prerequisites produce a structured blocker. Do not silently drop B/C or label A alone as completion of the original goal.
4. **Apply bounds to the whole closure.** The same cap applies to spec work, rootless work and remediation. If closure exceeds the bound, choose a smaller independently useful slice and rewrite its acceptance accordingly. If no such slice exists, show `needs decomposition`; never truncate a dependency chain or activate an oversized group under an unchanged goal.
5. **Use one validator for every entry point.** Automatic selection, proposal preview/commit, explicit activation, and active-sprint amendments call the same service. Validate ownership, approval provenance, grooming, criterion coverage, dependency closure, cycles, membership conflicts, role availability and bounds. A missing role blocks the affected candidate with a reason; a temporarily full slot only delays execution. Re-read mutable state and the sprint gate at activation rather than trusting a stale preview.
6. **Activate atomically.** The Core store transaction checks expected active-sprint identity and relevant revisions, writes the plan-to-sprint binding, creates the active sprint and attaches executable memberships together. Reject an existing unfinished active sprint; never archive it as a side effect of activation. Publish a wake hint only after commit. Duplicate activation returns the same sprint. A periodic sweep repairs a lost hint.

The shared Orchestrator validator performs policy and store lookups; the Core transaction performs database invariants only. No model or HTTP calls belong inside the transaction. A stale plan returns a specific conflict and is recomputed automatically, with a bounded retry, rather than being force-committed.

Planning bounds are not dollar authorization. The durable request ledger remains the spending authority; role slots remain execution concurrency. Do not invent reliable dollar estimates from task counts or let planning heuristics waive those controls.

## Active work, failures and follow-ups

Replace `InjectAdHocAsync` with a validated amendment operation. Consider a necessary prerequisite regardless of whether it has a parent. Preserve approval/grooming requirements and the active objective. Record why each addition is necessary and rerun closure/bounds. A discovery that changes the approved outcome becomes an intake draft; if it is essential to the current outcome, show that specific blocked dependency rather than allowing an unrelated initiative to proceed silently.

Remove the project-wide wait for every ungroomed follow-up. Wait only for the chosen outcome's necessary work. An unrelated documentation finding must not stop a ready approved outcome. Nonblocking related remediation can be grouped with complementary approved backlog at the next planning boundary. Blocking remediation may join the current sprint after grooming and validation.

Disable failure-aging-as-success. Age may trigger triage/escalation, but must not automatically close required work or satisfy dependencies. A Closed task needs an explicit disposition: delivered elsewhere with evidence, duplicate of a delivered task, or scope intentionally removed. Cancellation/abandonment does not satisfy a required criterion. Preserve Failed until an explicit allowed recovery/disposition; do not clear it just to keep the queue moving.

Apply the same distinction in `IssueStore.ReadyAsync`/open-blocker queries: an abandoned or ambiguously closed prerequisite cannot become runnable merely because its status is Closed. Model this as a typed closure disposition and, where needed, a legal lifecycle event; do not add an unrelated planner flag. Legacy Closed records without evidence need an explicit migration/compatibility decision rather than silently being treated as delivered.

Completion becomes a durable, resumable operation:

1. Validate criterion evidence and required task dispositions; terminal-row counts alone are insufficient. Merged code, tests and any required deployment/playtest evidence must match the claimed outcome. Do not invent tests or deployment requirements for unrelated documentation changes.
2. Atomically record successful completion plus a pending completion-bookkeeping record. A sprint whose outcome failed or was abandoned must not be presented as delivered.
3. A sweep processes pending bookkeeping for completed sprints independently of whether a sprint just became complete. It materializes/triages relevant drafts and finalizes the completion record.
4. Give each materialization a stable key derived from project and sorted draft IDs. Creation and draft disposition occur in one database transaction, or through a unique target mapping with resumable phases when issue/spec stores cannot share the operation. Reuse `IssueStore` mutation logic so events and the JSONL mirror remain correct; do not bypass it with raw queue inserts.
5. Failures retry the unfinished bookkeeping step, not the engineering work. Persist triage decisions before materialization so retries do not ask the model to regroup already-consumed drafts. New-initiative dispositions create draft proposals, never auto-approved specs.

## Minimal implementation sequence and touchpoints

| Increment | Concrete touchpoints | Exit condition |
| --- | --- | --- |
| 1. Repair planning invariants | `SprintAssembler.ListEligibleAsync`, `AssembleNextAsync`, `InjectAdHocAsync`; new shared validator; `SprintProposeService.CommitAsync`; `SprintStore` activation transaction; activation endpoints | Membership separates from readiness; all entry points reject ungroomed/stale/held/conflicting activation; no active sprint displacement |
| 2. Preserve authorization and define outcomes | Intake acceptance and `IntakeStore`; spec/groomer output contracts; proposal audit extension; follow-up triage `epic` branch | Accepted initiative provenance is durable; new initiatives stay drafts; planner can select a bounded slice across approved sources |
| 3. Make completion truthful and recoverable | `CompletionProgressAsync`, `SweepAgedFailuresAsync`, materializer; `FollowUpDraftStore`; transaction-capable `IssueStore` creation; task close disposition | Failed/abandoned work cannot masquerade as delivery; interrupted materialization resumes without duplicates |
| 4. Expose and qualify autonomous operation | Existing Sprints/Now planning state; event consumers/backstop; restart and end-to-end tests | UI explains the next outcome and real blocker; approved work plans/runs/reviews/completes without per-sprint approval |

Increment 1 can land correctness repairs while the current grouping remains temporarily conservative. It should not be advertised as full outcome planning until increment 2 is present. Preserve existing runtime integrations, worker verification, paid-call admission and merge/review gates throughout.

## Acceptance cases

| Case | Required result |
| --- | --- |
| Forge suggests a new feature | Expanded intake draft only; no approved spec or executable task |
| Operator accepts one initiative | Derived design/grooming/planning proceeds without another sprint approval |
| A -> B -> C delivers one approved outcome | One sprint contains all three; dispatch runs only ready tasks |
| Twelve-task spec exceeds the selected bound | Smaller demonstrable slice or `needs decomposition`; no uncapped activation |
| Related fixes originate from sibling tasks/different approved specs | One outcome may include both, with explicit criterion links |
| Unrelated ungroomed follow-up exists | Ready independent candidate can activate |
| Parented approved prerequisite appears during active sprint | Validated amendment adds it; dependent task remains unclaimable until ready |
| Dependency is unapproved, failed, cyclic, missing or cross-project | Affected outcome blocked with an actionable explanation; no silent prerequisite omission |
| Proposal API submits arbitrary/ungroomed IDs or gate is held | Same validator rejects it; old sprint unchanged |
| Two activation attempts race or a stale preview is committed | One activation or idempotent replay; no partial memberships or archived unfinished sprint |
| Age threshold passes on a failed required task | Still failed/blocked; no completed outcome or unblocked dependency |
| Operator closes required work as abandoned | Outcome remains unmet or explicitly partial/cancelled; no false success |
| Crash after completion, after one target creation, or before draft disposition | Restart resumes bookkeeping exactly once; no orphaned/duplicate follow-up |
| Triage outage | Persisted drafts remain retryable; unrelated approved work is not globally stalled |
| Late paid budget hold or unavailable required role | Explain the real constraint; do not reset budget, weaken approval or create new initiative work |

## Settled urgency decision (2026-11-17)

Finish the current sprint unless the operator explicitly interrupts it. Unrelated urgent initiatives or work wait for later selection; priority P1, a blocker label, and requeue metadata alone do not permit automatic injection. Existing groomed ad-hoc prerequisites with an actual blocks edge into active work can join under normal validation. Ordinary follow-ups remain deferred until their existing later-sprint path; a followUpOf relationship alone does not establish necessity. No interruption UI or new manual-action semantics are introduced in this increment. Richer objective-based amendment validation remains proposed planner work.

## Remaining policy choices

The core direction is settled; these are defaults to make explicit rather than reasons to stop the invariant repairs:

- **Initial size limit:** propose at most 10 engineering tasks including prerequisites, applied uniformly. Unknown effort is visible and encourages smaller slices; task count is not a substitute for effort or spend estimates.
- **Abandoned outcomes:** propose a distinct cancelled/partial disposition in sprint presentation rather than overloading Completed or silently archiving work. Specify which operator actions may remove a required criterion.
- **Scope ambiguity:** propose draft/exclude when nonessential, and an explicit blocked decision when essential. No automatic approval of a new initiative because a model calls it remediation.

These defaults require agreement before their product-facing behavior is implemented. The urgency rule above is settled; the remaining design proposals are not implementation authorization.
