# Completion, delivery evidence, and explicit interruption

Implemented source contract for schema 41. This document does not claim live
migration, browser acceptance, or deployment qualification of this increment.

## What completion records

The project-local `sprint_completion` receipt separates terminal engineering
bookkeeping from verified delivery. Finishing the sprint and creating its receipt
share one transaction. That transaction rechecks exact executable membership,
member status/metadata, and the latest committed typed plan identity. Missing
members are an integrity error. Empty historical sprints can finish bookkeeping
but cannot claim delivered outcomes.

The outcomes are deliberately distinct:

- `LegacyTerminalOnly`: historical terminal work without an approved typed plan.
- `Incomplete`: required work was closed without evidence of delivery.
- `PendingEvidence`: a typed outcome still needs verification, a required
  deployment, or explicit operator observation.
- `Satisfied`: the recorded plan's required evidence has been met.

`ReadyForDeployment` is eligibility, not deployment success. `DeliveryRequired`
is captured from routine deployment policy or explicit post-deployment criteria.
Projects with no deployment requirement do not acquire an invented deployment
gate. The deployment store remains the authority for candidate/release status.

Controller publication records bind the exact published head to the worker's
typed verification receipt. Merge observations bind task, PR, head and actual
merge SHA. A successful merge request followed by a stale or incomplete GitHub
response leaves the watch live. A task marked Completed alone is not verified
delivery evidence.

These are trusted-tool workflow guarantees. Worker SQL identities retain generic
issue-table DML, so task metadata is not a hostile-worker provenance boundary.
Workers are denied writes to completion, criterion evidence, committed plan audit,
initiative authority, and deployment tables. Independent GitHub/CI/release
qualification remains necessary for deployment.

## Follow-up recovery

Each bounded triage batch claims its exact source drafts uniquely before invoking
a model. The decision is durable before materialization. Creating a remediation
task, consuming its source drafts, and recording item identity share one
transaction through IssueStore mutation/event semantics. Replay does not create
duplicate tasks. New initiative suggestions remain unapproved proposals.

An interrupted or uncertain model request becomes `NeedsReview`; it never repeats
automatically. The explicit retry API acknowledges that a new request may incur
cost. Persisted decisions can retry materialization without another model call.
A failing batch is visible and does not prevent other batches from being checked.
Late drafts are found through the receipt even after the active sprint changes.

Legacy proposal-source recovery runs before new completion claims and cannot
consume drafts already owned by a completion batch. Unclaimed drafts beyond a
batch's bound keep bookkeeping unfinished. `NeedsReview` remains visible but does
not itself indefinitely stop independent approved work.

## Observation and interruption

An `OperatorObserved` criterion is explicit external verification, not an
automatic visual check. Its append-only evidence binds exact completion, plan
hash, criterion, deployed source, candidate, proof reference/hash, operator
provenance and time. The dashboard endpoint also requires that its running source
revision matches the submitted deployed source. The current endpoint therefore
qualifies observations of this running dashboard, not arbitrary external apps.
Shared dashboard authentication is recorded honestly as a shared operator
identity; it does not identify an individual human.

Explicit abandonment records an immutable reason and authenticated provenance for
the exact unresolved plan. It preserves unmet evidence and is never a successful
delivery stamp. It stops remaining completion bookkeeping, retains drafts/claims
for audit, and prevents subsequent source/candidate admission or triage retry.
Running triage and nonterminal independent releases must finish or be explicitly
stopped/rejected first. Abandonment affects this completion, not the entire
approved initiative.

Automatic and explicit activation must use the same Core completion barrier.
Pending required delivery or unresolved typed outcomes hold the next sprint;
explicit abandonment permits interruption. Old terminal-only history does not
silently become a permanent global stop.

## Validation and release boundary

At source `b39c0b7`, 142 focused offline tests pass. They cover exact plan and
source binding, rollback on consumption failure, concurrent replay, late batches,
uncertain paid requests, immutable operator proof, abandonment races, project
isolation, authentication, and stale post-merge observations.

M041 is additive and Workload-only; the registry migration advances its version
without creating project-local completion/deployment tables. Both fresh and
idempotent upgrade paths include the added fields. A separate reviewed schema
bridge and isolated Azure SQL rehearsal are required before production upgrade.
The source test receipt is not a substitute for that qualification.


## Interrupted grooming recovery

Manual grooming now persists a queued `issue_groomer_run` before sending an existing groom wakeup hint. The normal scheduler owns execution and honors the groom hold. A controller restart marks interrupted Started runs NeedsReview; neither failed nor uncertain runs are automatically started again. No schema change is needed: the existing string status and trigger columns carry Queued, NeedsReview, and Retry.

The specification detail exposes the latest run ID and an explicit retry action. Retry compares that exact previous attempt, rejects duplicate admission, and queues work without running a model in the HTTP request. A crash before the specification entered Grooming is also recoverable through this action. The prior paid request may have completed, so the UI must retain its uncertainty/cost explanation.

Safe retry reads at most 100 descendants from the owning project's exact specification tree. Only Pending stories/tasks can be resumed. Their IDs and current mapping state enter the groomer context; exact repeated create calls reuse those IDs. Completion validates outcome bindings for every Pending descendant before publishing Groomed. Explicit force re-decomposition remains a separate action, permits only terminal historical rows, and also validates all remaining Pending descendants. The generic specification PATCH cannot invent Grooming/Groomed completion.

Offline qualification covers queue/retry CAS, interrupted-run recovery without automatic generation, exact row reuse and missing mapping refusal, project ownership, preserved parked work rejection, HTTP stale/duplicate retry, and generic status bypass rejection. SQL Server admission uses a serializable transaction and update/range locks on the latest-run query; actual Azure SQL qualification is recorded separately by the integration review.
