# Unattended workflow review

This review separates source-confirmed behavior, offline reproductions, live UI
observations and measured timings. It does not claim an unattended UI-originated
delivery has passed. The prior pilot started at a task API and qualified a narrower
engineering/deployment path.

Recommended order: correct usage accounting; repair delivery visibility and
outcome correctness; establish one bounded outcome planner; make normal delivery
automatic within agreed project scope; qualify the full UI journey; then invest
in model benchmarking. TimeLoop and dedicated GitHub agent identities remain later
projects.

Reports:

- [UI journey and product direction](ui-journey.md)
- [Sprint planning and seven offline reproductions](sprint-planning.md)
- [Performance, query growth and refresh behavior](performance.md)
- [Usage accounting](budget.md)
- [Budget release compatibility](budget-release.md)
- [Completed live budget correction and release qualification](budget-live-qualification.md)
- [Completed live workflow repair qualification](workflow-live-qualification.md)

Top issues to decide together:

| Area | Evidence | Recommended direction |
|---|---|---|
| Planning | Ancestry grouping splits dependency chains and related follow-ups; an alternate API bypasses eligibility | One bounded outcome planner with shared deterministic activation checks |
| Unattended recovery | Unrelated grooming can stall all assembly; completion can strand follow-up drafts; aged failures close automatically | Recoverable, idempotent handoffs; explicit blocked/partial outcomes |
| Delivery | Live Deployments cannot deserialize its history; routine release remains a manual policy | Repair the contract, then automate qualified releases under explicit project policy |
| Comprehension | Closed is shown as done; finished items counted in flight; entry form misstates next step | One truthful request journey and clear next action |
| Performance | Sprint board downloads all history and skill bodies; per-sprint SQL reads grow with history | Bounded screen queries, paged history and coordinated refresh |
| Spending | Prior $24.81 is conservative internal accounting, not billed spend; user reports about $1 | Settle from detailed API usage and verified tariffs, retain raw usage, reconcile billing separately |

## Agreed product authority

The operator clarified: new initiatives enter through their intake conversations.
Forge may suggest initiatives in a proposal area, but a suggestion must be expanded
through intake (or an equivalent conversation) and approved before ingestion.
Forge must not turn the standing vision into unapproved execution. After approval,
Forge manages planning, related backlog/remediation, implementation, verification
and delivery within the approved scope. Routine work should be unattended; product
decisions remain with the operator.

The operator also confirmed the historical pilot cost is **$1.01**. Record it as an
operator-reported aggregate correction, preserving original request rows and the
$25 ceiling. It is not independently retrieved provider billing evidence.

No additional paid requests were made for this review.
