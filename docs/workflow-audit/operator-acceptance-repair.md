# Operator acceptance stays with the implementation outcome

The live pilot's grooming run created an implementation unit and a separate
unit consisting only of post-deployment operator observations. Every
executable sprint member must be merged and verified before deployment, so
the latter unit could never truthfully progress. It must not be repaired by
inventing code, weakening completion, or claiming observations from tests.

The admission rule now requires each engineering task's own criterion mapping
to include genuine `MergedAndVerified` work. Later `DeploymentHealthy` and
`OperatorObserved` criteria belong to the same implementation outcome. The
grooming contract explains this distinction and receives current project
worker capacities as a soft parallelism preference.

## Existing untouched plans

The specification page offers a reviewable correction when the exact typed
mapping proves one implementation unit and observation-only QA rows. It shows
the tasks being retained and retired, the exact transferred criteria, and all
resulting acceptance criteria with their original approved excerpts. It makes
no paid model call and does not release a stage hold or start execution.

The repair requires an intact approved initiative, the current groomed body,
the latest successful owning-store grooming run, and untouched pending rows.
It refuses execution/publication metadata, agent runs, lifecycle events,
existing membership or committed sprint audit, hidden descendants, ambiguous
or external dependencies, inconsistent units, incomplete criterion coverage,
and dependency cycles. Historical inactive membership deleted without any
durable audit cannot be reconstructed; the guard checks all recorded history.

A serializable transaction rechecks a preview hash over the full affected
rows, dependencies, specification/run coordinates and resulting bindings.
IssueStore owns the binding updates, closure and event writes. Exact operator
criteria transfer unchanged, old rows remain Closed with Cancelled disposition,
and a durable receipt retains the before/after mapping. These cancelled rows
do not count as delivered or satisfy dependency obligations. An identical
request replays the receipt; changed coordinates are refused. The normal
activation transaction rejects stale bindings if repair wins the race, and
repair refuses membership if activation wins.

## Qualification

Forty focused integrated tests passed across Core, HTTP, component state,
phase admission, capacity grounding and specification scope. They include
atomic rollback, stale preview rejection, concurrent repair/activation,
project-isolated endpoints, navigation during a pending request and truthful
completion. Production and UI builds were clean. An actual offline browser
session reviewed the desktop and 390-pixel layout and applied the correction;
two implementation tasks remained pending and the two observation rows became
Closed. The page explicitly retained the need for real post-deployment evidence.

Actual Azure SQL qualification used a fresh isolated Workload schema and
source `bb83c60c6cd6b378372dad1c6e6c67dc1caf1600`. All seven cases passed:
exact transfer/replay, stale-input refusal, trigger-injected rollback,
concurrent identical repair, repair-versus-activation race, both deterministic
activation orderings, and the actual completion coordinator. Implementation
became ready for deployment while the outcome remained `PendingEvidence` and
had no deployment candidate. The temporary schema was removed and its absence
verified. Production data and paid models were not used.

Proof identifiers:

- Program SHA-256: `abaece76b76f0e844a50f358838119ec4e40da19f0bf13e9fb7a5dbac9b2fa1e`
- Core assembly SHA-256: `ee413d533ffdf8fdb33f990257f363a48575cc17227a0c5637c30346eeb7abe9`
- Results SHA-256: `02c57ccfb15839bbabc6f474304646c51215b3292c0a88a233e3a3e948c04863`

These are recovery qualifications, not evidence that the live initiative has
finished engineering, deployed its change, or passed operator acceptance.

The same browser session exposed a legacy manual **Ship** action that could
label a Groomed specification Shipped while every task was still Pending.
The button is removed and API/store writes reject new Shipped labels.
Historical rows remain readable; no single sprint is allowed to relabel a
multi-outcome specification. Sixty-one integrated recovery/specification tests
passed after this correction, and an actual page reload confirmed the button
is absent and task links retain their owning project.
