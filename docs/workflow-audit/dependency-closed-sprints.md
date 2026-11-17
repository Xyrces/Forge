# Dependency-closed sprint membership

This slice separates planning membership from dispatch readiness. A groomed candidate may enter a sprint together with its necessary, recursively validated prerequisites. Dependency edges still block dispatch until their prerequisites are Completed or Closed under the existing task semantics; Closed is not evidence of delivery.

The automatic assembler retains its existing spec/theme grouping and priority ordering. It finishes the current sprint first. An active sprint admits only necessary prerequisite amendments, including groomed parented work and transitive prerequisites. Unrelated urgency, requeue metadata and follow-up provenance do not authorize interruption. Ungroomed follow-ups remain visible as waiting work but do not hold an unrelated coherent candidate that can proceed.

All added work must be Pending, groomed through its own valid ancestry, owned by the selected project and outside another active sprint. Missing, Failed, Blocked, unavailable or cyclic prerequisites reject the candidate rather than dropping the prerequisite. A rejected candidate does not prevent evaluating another coherent candidate. The existing stage gate remains in force.

Proposal previews explain included prerequisite IDs and fingerprint the full closure, ancestry, specifications and incoming dependency edges/statuses. Approval of the proposal revalidates that evidence. The membership transaction checks exact incoming edge/status snapshots and existing active membership, then commits the complete membership atomically. A change between planning and commit rejects the stale snapshot. No new schema is required.

Dependency reads use batched queries, including terminal and missing prerequisite rows. A 1,000-task validation bound fails explicitly; it is a technical graph safety bound, not an invented sprint size policy. Existing proposal and automatic candidate grouping remain deterministic. This is not yet a semantic outcome planner or proof that an arbitrary specification's task breakdown is independently deliverable.

Offline qualification covers whole-spec chains with dispatch blockers preserved, transitive parented amendments, unrelated follow-up/urgency deferral, failed/ungroomed/cyclic closure rejection, stale proposal evidence, exact membership and dependency transaction races, and candidate starvation prevention. Actual Azure SQL qualification of these new transaction paths remains separate from SQLite regression results.
