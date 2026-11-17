# Durable initiative approval implementation

This implementation separates a proposal from permission to execute it. Forge-generated initiative suggestions appear in Intake's Proposals area. **Expand in Intake** creates or resumes one linked conversation and preserves the source findings; it creates no issue, spec or sprint member. The existing direct Intake conversation remains available for operator-originated work.

The proposal panel lists bounded summaries, then fetches full content for the selected proposal. Approval submits the visible proposal's stable ID, exact revision and content hash. A concurrent revision causes a conflict; the operator must review the new content. Model tools may create or revise unapproved drafts by stable ID, but cannot approve them or mutate an already-approved spec. The private dashboard has no authenticated user identity, so approval attribution explicitly says “operator via private dashboard.”

Approval is durable before ingestion. The owning project's transaction creates one epic, one Draft spec and an ingestion receipt. A restart between that commit and acknowledgement in the primary proposal store adopts the same receipt. Repeated approval returns the same outcome. New initiatives do not become members of an unrelated active sprint.

The refinement reconciler starts with the active controller and checks durable pending work at startup, on event hints and every 30 seconds. It processes bounded pages. A lost event does not lose approved work. A receipt is claimed before a model call; successful completion and the transition to ReadyForDesign commit together. Existing engineering grooming, sprint, architecture, review and merge checks still apply afterward.

Construction alone starts no refinement. Program starts and joins the reconciler after controller admission opens. A Product response without a successful update of the current owned spec is not completion; tool writes are bound to that spec/project and the server's canonical product-run author.

An interrupted Running refinement is uncertain and becomes Failed on controller recovery. Failure does not automatically authorize another paid request. The panel shows the failure and an explicit **Retry refinement** action, including the possible additional cost. The existing durable budget ledger remains authoritative for all request costs and liabilities; these receipt states do not erase them. A model run that produces no committed spec update also requires explicit retry.

Forge follow-up initiative suggestions use persisted source bindings. Each tick recovers already-created suggestions before any new triage, including after the source sprint has completed; source groups are consumed atomically. Overlapping groups in one triage response are rejected before any proposal is created. This relies on the existing single-controller ownership fence. A failed acknowledgement is retried as a database write without repeating generation.

Legacy message-based acceptance is rejected. **Review legacy draft** imports its payload as an unapproved durable proposal; the operator must then approve its exact revision. Old conversation text remains history and is never treated as an approval credential.

Validation includes typed-store and HTTP cases for exact-revision conflicts, repeated acceptance, project isolation, bounded summaries without full bodies, expansion without work, lost-event recovery, committed-target adoption, forged event hints, interrupted/failed refinement, and processing beyond the first pending page. All model behavior in these tests is scripted or supplied by typed delegates; no paid requests are made.

Schema40 is required. This code and offline tests do not qualify a live schema upgrade or schema39 rollback by themselves. The separate deployment/migration qualification must establish that boundary before deployment. Existing whole-conversation/session listing remains a separate read-path scaling concern; the new proposal panel does not depend on exporting all proposal bodies.
