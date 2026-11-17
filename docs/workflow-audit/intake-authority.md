# Initiative authority and durable Intake approval

Read-only design review against `1c54c63`, with workflow-clarity presentation changes layered separately. This document proposes an implementation; none of the proposal/approval persistence below exists merely because it is described here.

## Settled authority and navigation

The user introduces initiatives. Forge may suggest initiatives, but suggestions must be expanded in Intake and explicitly approved before ingestion into executable planning work. A model's recommendation, generated proposal, or triage result is not user approval.

Add a **Proposals** area for Forge suggestions, with title, source findings, project, and current discussion/approval status. Its primary action is **Expand in Intake**: open or resume the proposal's linked Intake conversation, preserving provenance. It must not create an epic, spec, sprint member, or engineering task. User-originated initiatives can still start directly in Intake. The final reviewed proposal has a separate explicit approval action; only that action authorizes ingestion. Link the resulting epic/spec back to its exact approved proposal revision.

The minimal navigation change can be a Proposals view within the existing Intake area, rather than a separate planning application. Existing Backlog task creation and remediation-task flows are separate from new initiatives; this increment must not silently redefine all tasks as initiatives or require approval of every corrective task.

## Confirmed implementation gaps

### Follow-up triage bypasses initiative approval

[`SprintAssembler.MaterializeFollowUpsAsync`](../../Orchestrator/Sprint/SprintAssembler.cs#L609) consumes follow-up triage decisions. Its `case "epic"` at line682 creates a spec directly and calls `SetStatusAsync(...Approved)` at line690. No Intake session, durable user approval, or accepted epic is required. This is an actual bypass of the settled initiative authority rule, even though the branch's name says epic and its concrete output is a spec.

Replace that branch with durable suggestion capture and provenance links. Do not merely change the prompt to ask the model to seek permission. The fallback and task dispositions should retain their existing grooming behavior unless a separate policy change is approved.

### Intake acceptance is not crash-safe or concurrent-idempotent

[`AcceptProposedEpicAsync`](../../Agents/IntakeAgent.cs#L404) reads the session and proposal message, then searches for a prior system-message receipt at line414. A matching receipt returns the issue immediately at line418. Without it, the method creates an epic at line431, finds or creates its spec, optionally links the epic to an active sprint, and only then appends the acceptance chat receipt at line498. The refinement event is published at line503.

Consequences established by the ordering:

- Two concurrent requests can both observe no receipt and create different epics.
- A crash after epic/spec creation but before the chat receipt permits duplicate creation on retry.
- A crash after the receipt but before event publication leaves no durable refinement request. Reaccept returns early, so it does not repair that missing publication.
- The prose receipt is doing double duty as UI history and an idempotency key; neither its uniqueness nor atomicity is enforced by the database.

[`ProductRefinementQueue`](../../Agents/ProductRefinementQueue.cs#L48) subscribes to the in-process dashboard bus and handles `intake.epic.accepted` at line60; refinement runs at line92. Event delivery is not a durable pending-work ledger. Its comment suggesting reaccept as recovery does not match the acceptance early-return behavior.

### Proposal revisions lack an acceptance boundary

`create_epic` currently stores a draft message rather than creating an issue. That is the correct direction. [`CreateEpicAsync`](../../Agents/IntakeAgent.cs#L560) appends a revised proposal message when a matching title changes. Title matching and UI card collapsing do not establish a stable proposal identity/revision or reject approval of an obsolete message. The accept endpoint still accepts any message with proposal payload present in that session.

The replacement must bind approval to the exact visible content, not a normalized title or mutable latest-draft lookup.

### The stores are not one transaction today

[`ForgeComposition`](../../Orchestrator/Composition/ForgeComposition.cs#L528) constructs IntakeStore from the primary issue store. Per-project Intake agents receive the target project's issue store at line554 and a project-routing spec facade. A primary-session write and a target-project issue/spec write cannot be assumed to share a transaction across the existing provider abstractions. Production SQL schemas share a database, but development stores and current APIs do not provide a common transaction contract.

`IntakeStore.SetMessagesAsync` also deletes and reinserts message rows, changing their generated IDs. It is not called by the current production Intake agent, so this is not evidence of a current rollback defect; it is a reason not to use message IDs as the new durable proposal identity.

## Proposed minimal durable contract

1. **Proposal revision record:** stable proposal ID, revision number, owning project, linked Intake session, origin (`user` or `forge`), source draft/finding IDs, immutable title/body/priority snapshot and canonical content hash. A revision may be current, superseded, or approved; the database enforces at most one accepted ingestion per exact proposal/revision.
2. **Explicit approval record:** proposal ID/revision/hash, approval time, and operator attribution available from the current control surface. Do not invent authenticated-user provenance: the current private dashboard has no user login. Approval uses compare-and-swap against the current unapproved revision; stale content returns a conflict. Repeated approval returns the same durable outcome.
3. **Project-local ingestion receipt:** unique proposal ID/revision plus epic ID, spec ID, and ingestion/refinement progress. The owning project's transaction creates the epic, Draft spec, and receipt together through transaction-aware store APIs. No direct dashboard SQL writes bypass IssueStore or its event/mirror responsibilities.
4. **Resumable ingestion:** scan approved proposals lacking acknowledged ingestion. Look up the target project's unique receipt before creating anything. If target commit succeeded but primary acknowledgement failed, adopt the existing receipt. This avoids a distributed transaction while preventing duplicate executable work.
5. **Durable refinement request:** the ingestion receipt records pending/completed refinement with retry/error evidence. Existing events become wake-up hints. Startup and periodic reconciliation resume pending work after missing events or crashes. Persist completed refinement before treating the stage as finished; avoid duplicate paid refinement on every notification.

Keep the immutable approval payload independent of the conversation's mutable presentation. Chat messages may display the approval and result, but they are not the authority or recovery record. Source follow-up drafts must be linked/consumed idempotently with suggestion creation so repeated sprint completion does not create duplicate suggestions.

## Required acceptance cases

- A Forge epic suggestion creates a proposal only; no Approved spec or executable engineering work exists before explicit approval.
- Expand in Intake resumes the same suggestion conversation on repeated clicks and preserves project/source links.
- Editing a proposal creates a new revision; approving an older revision/hash fails visibly without ingestion.
- Concurrent approval requests produce exactly one epic/spec pair and one target receipt.
- Crash before target commit rolls back all target creation; crash after target commit but before primary acknowledgement adopts the same result.
- Lost acceptance event and restart resume pending refinement from durable state.
- Two projects with colliding local issue IDs remain isolated; proposal project cannot be rebound at acceptance.
- Repeated materialization of the same source findings does not create duplicate proposals.
- Approval is recorded as an explicit operator action; a model tool cannot grant it.
- Existing technical grooming, architecture/plan checks, sprint eligibility, review and merge checks remain enforced after ingestion.

## Migration and scope boundary

The proposal and ingestion records require an additive schema migration in both SQL Server and SQLite paths, updated expected-version registration, and tests of fresh and upgraded stores. If the next migration is schema40, the current schema39 runtime **does not** thereby support40. It needs a separately qualified compatibility/release plan, including the rollback floor and safe migration ownership; the supervised39 migration does not constitute a reusable unattended upgrade implementation.

The correctness work is durable exact-revision approval, idempotent project-local ingestion, recoverable refinement, and removal of the confirmed automatic initiative-approval bypass. This does not authorize new defaults for initiative scoring, suggestion frequency, autonomous acceptance, sprint capacity, semantic grouping, dependency policy, failure aging, or paid retry budgets. Those choices must remain separate and explicit.
