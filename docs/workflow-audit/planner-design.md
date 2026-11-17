# Bounded outcome planning: implemented contract and qualification

Status, 2026-11-17: the outcome planner, durable completion, routine-release integration and completion controls are implemented in the unattended-workflow integration source. This is no longer an unimplemented proposal. Offline tests and synthetic dashboard checks support the behaviors described below; the strict schema-41 production cutover and the new UI-originated end-to-end pilot remain qualification work until their actual receipts are recorded. Passing source tests is not a claim that the new planner has delivered the live pilot.

The original audit described the code at `8460dde`. Its weaknesses and proposed increments are historical context, summarized at the end. They must not be read as a list of defects still present in the integrated source. See [routine delivery](routine-deployment.md) for release authority and [the earlier performance review](performance.md) for the original read-path findings.

## Approval and scope

New initiatives enter through operator Intake. Forge may propose an opportunity, draft alternatives and ask questions, but its suggestions remain drafts until the operator approves the exact latest revision and content hash. Approval authorizes the initiative's outcome; design, grooming, bounded planning, engineering, review and ordinary in-scope remediation then proceed without a separate approval for each sprint.

`Core/Initiatives/` persists that approval and idempotent project-local ingestion. Refinement must actually update the bound specification before its durable receipt advances. A new-initiative follow-up becomes an Intake proposal, never an automatically approved specification. A task's title, parent relationship, `groomed=true` flag, or a model's assertion that work is remediation cannot create scope authority.

Grooming uses its existing model call to publish typed `sprintOutcome` metadata: an explicit outcome/objective, complete required member set, observable criteria and evidence kinds, each member's contribution, and exact approved scope references. `SprintOutcomeAuthority` checks the owning project's approved-ingestion revision/hash, completed refinement and intact accepted content. Literal excerpts make the mapping reviewable; text matching does not prove semantic coherence. That judgment remains grooming's responsibility. Missing legacy mappings fail closed and require scope repair.

## Selection, bounds and parallel work

A sprint is a small independently demonstrable outcome within approved scope. An epic/spec boundary is provenance, not a mandatory sprint boundary: complementary tasks from different approved sources may share a sprint when their explicit criterion mappings describe the same outcome.

**Ten engineering tasks is the configurable ceiling, not a target.** It includes necessary prerequisites and applies uniformly. The selector prefers a small complete delivery unit. A soft preference may add naturally independent, explicitly related units up to the project's configured implementation capacity, respecting each role's limit. There is no fixed task-count minimum. It does not pad to ten, manufacture splits, include unrelated work, or count a sequential chain as parallel capacity. Current occupied slots are not a planning input. Task counts do not estimate reliable effort or authorize spending; the durable budget ledger remains the paid-call authority.

`SprintPlanBuilder` reads bounded projections: at most 100 discovery candidates and 100 explicitly same-outcome candidates, followed by targeted dependency/ancestor reads. A keyset cursor advances discovery beyond ineligible old work. Deterministic results are cached by observed candidate, snapshot and configured-capacity inputs. This is an explicit bounded window, not exhaustive optimization of the entire backlog. No additional planner model call is introduced.

Membership and dispatch readiness are separate. A dependency chain A → B → C can belong to one sprint while only A is initially runnable. The validator closes over required prerequisites and rejects missing, failed, blocked, cyclic, foreign-project, ungroomed or unapproved necessary work. It never truncates a required delivery unit to fit the ceiling. An oversized unit needs a smaller useful decomposition through grooming. A prerequisite whose declared required partner is absent from its dependency closure is also deferred for correction rather than silently dropping that partner.

Grooming now receives the owning project's current CoreDev and ClientDev
slot limits on each run, using the same role defaults as execution. Read-only
QA and Reviewer slots do not count as implementation task capacity, including
in the selector's soft parallelism score. The prompt describes a soft
parallel goal derived from those configured limits, when the approved outcome
supports independent work; fewer tasks remain valid. This closes
the earlier gap where the selector knew capacity but the grooming model did not.

Every executable member must contribute to a genuine `MergedAndVerified`
criterion. Later `DeploymentHealthy` and `OperatorObserved` criteria stay in
the same implementation outcome; they do not create observation-only
engineering tasks or require fabricated PRs. Actual later evidence remains
mandatory after deployment.

The legacy manual **Ship** action is retired: a specification label cannot
stand in for implementation or deployment evidence. API and store writes
reject newly marking a specification Shipped; historical rows remain readable.
Multi-outcome specifications are not automatically relabelled from one sprint's
success. Follow completion and deployment evidence for the actual outcome.

## Activation and preservation of the current objective

`SprintActivationValidator` is shared by automatic selection, proposal commit, generic activation and active membership amendment. Proposal commit accepts the complete recorded membership and canonical objective; it cannot substitute arbitrary task IDs or goal text. `SprintStore` atomically writes memberships and the typed `weights_json.plan` audit while rechecking issue/spec snapshots, dependencies, current approval authority and active-sprint exclusivity. The plan hash binds criteria, members, grooming mappings, deferrals and input fingerprint. Amendments add an audit version rather than overwriting history.

The current sprint keeps its objective. Necessary groomed prerequisites may join after the same approval, closure and transactional checks, including parented or transitive prerequisites. Unrelated P1 work, blocker labels, requeue metadata and a `followUpOf` relationship alone do not justify injection. Ordinary unrelated follow-ups wait for later selection; their mere existence does not globally hold a coherent approved candidate.

Generic edit/delete routes cannot erase committed audit-backed objectives or required membership. Before claiming engineering work, dispatch checks typed-plan membership, unchanged grooming binding and exact approval authority. Watch lifecycle processing remains exempt. Stage holds are re-read at evaluation; no cross-store atomic gate guarantee is claimed.

## Completion, interruption and recovery

A sprint's terminal task counts do not prove delivery. `IssueResolution` distinguishes Completed work from Closed work with an explicit disposition. Cancellation and ambiguous legacy closure do not satisfy dependencies. An evidenced delivery elsewhere or duplicate of completed work has different semantics. Failed/Blocked work remains unresolved until an explicit allowed operator action; elapsed time does not manufacture success.

`SprintCompletionStore` records the exact committed plan, member/evidence snapshot and durable bookkeeping receipt together with terminal sprint bookkeeping. The receipt distinguishes `Satisfied`, `PendingEvidence`, `Incomplete` and `LegacyTerminalOnly`. Publication evidence is bound to the verified worker result and published PR head; the watcher records an actually observed merge. A successful merge request followed by an incomplete/stale GitHub observation keeps the watch live instead of losing the evidence-recovery path.

Evidence requirements are separate:

| Requirement | What it establishes |
| --- | --- |
| `MergedAndVerified` | Recorded implementation verification and the matching observed merge |
| `DeploymentHealthy` | The exact qualified source's successful independent release |
| `OperatorObserved` | Explicit operator observation of that deployed source, with immutable proof reference/hash and authenticated provenance |

Engineering evidence can be satisfied before a required routine release finishes. `DeliveryRequired` captures enabled routine-release policy or explicit post-deployment criteria; projects without either are not forced into a nonexistent deployment path. The shared activation barrier checks the necessary bookkeeping and delivery/acceptance facts, not only terminal counts or one success label. `DeploymentStore` remains the release lifecycle authority; the completion receipt binds to it and projects its result. A separate paged backstop recovers a stop after the deployment row became terminal but before completion evidence advanced.

Completion reconciliation runs independently of the active-sprint event. Source drafts have durable claims; decisions are saved before targets are materialized. Target creation, source consumption and claim acknowledgement share a transaction. Stable identities recover replay without regrouping or duplicating already-consumed work. Legacy unclaimed proposal sources are reconciled before new completion claims; legacy recovery cannot consume a durable batch's claimed sources. A failed batch does not starve unrelated later batches. Late drafts and multiple bounded batches cannot prematurely mark bookkeeping finished.

An interrupted paid triage request becomes review-needed; it is not blindly repeated. The UI explains that the previous request may have completed and another request may incur a charge. Explicit retry is bound to the selected completion. Recorded decisions resume materialization without another model call. Uncertain nonblocking follow-up triage remains visible without inventing evidence that it completed.

An operator may explicitly stop an unresolved typed terminal outcome with its exact plan identity and a required reason. The immutable interruption preserves the original evidence/outcome and does not claim delivery. It stops future bookkeeping/generation for that outcome. Running triage and a nonterminal independent release cannot be cancelled by pretending they finished: an unstarted release may first be safely rejected, while authorized/running release work must reach its safe result. This completion action is not a generic cancellation mechanism for arbitrary active engineering runs.

## Operator presentation and qualification limits

The Sprints page separates the task board, engineering evidence, follow-up processing and release state. Historical task boards are read-only; explicit completion review/retry/interruption actions remain available where the server permits them. The collapsed outstanding-outcome overview reads five joined summaries per page, uses sprint names, and can select an off-page historical sprint. Selected batch history is independently bounded. Loading failures show unavailable evidence rather than implying empty history or successful delivery. Routine deployment hold/release remains on the canonical Flow controls.

Qualification must preserve all of these cases:

- A complete approved chain stays together; dispatch only runs dependency-ready members.
- A small useful unit is not padded, while natural independent approved work can use configured role capacity.
- Over-ceiling, stale, ungroomed, foreign or incompletely authorized units fail without partial activation.
- Activation races have one valid result; stale plan/member changes roll back membership and audit together.
- Cancelled/unknown Closed work does not unblock a required dependency or become delivered by age.
- Crash/replay around source claim, decision, target creation, consumption and terminal release projection does not duplicate work or paid generation.
- Wrong plan/source/criterion proof and unqualified release evidence cannot satisfy acceptance.
- Interruption racing release binding has one authority winner; it cannot leave an eligible orphan release.
- Project/sprint switching and failed UI loads cannot display stale evidence as the new selection's result.

The [schema-41 rehearsal](completion-sql-qualification.md) passed with disposable Azure SQL schemas and the old read-compatible bridge before/after additive migration, including the integrated grooming recovery checks. The live cutover remains separate qualification. Synthetic browser proof does not substitute for the live operator's observation of the pilot's exact deployed version. Routine schema-preserving delivery, schema-upgrade coordination and whole-host disaster recovery are distinct claims; this planner work does not qualify the last of them.

## Historical design and superseded baseline findings

At `8460dde`, planning grouped broad spec work, could bypass complete outcome/approval checks, and relied on volatile completion/materialization sequencing. The original design proposed four increments: repair activation invariants; persist approval and outcome mappings; make completion truthful and durable; expose and qualify the autonomous flow. Those source increments now have concrete owners in `Core/Initiatives`, `SprintOutcomeAuthority`, `SprintPlanBuilder`, `SprintActivationValidator`, `SprintStore`, `Core/Completion`, the routine deployment coordinator and the existing dashboard surfaces.

The earlier statements that approval was only a conversation-message convention, triage-created epics were auto-approved, durable completion was merely proposed, or no explicit outcome interruption existed described that baseline. They are not current implementation claims. The original 2026-11-17 finish-current decision remains in force: urgency alone never silently replaces the committed objective. Further semantic-model improvements, exhaustive backlog optimization, reliable effort/cost forecasting and whole-host recovery remain outside this implementation; none may weaken approval, verification, budget admission or the release protocol.
