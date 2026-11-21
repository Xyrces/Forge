# Unattended workflow review

The approved UI-originated Forge pilot is now **Satisfied**, with automatic
exact-source delivery and both operator acceptance criteria recorded through the
UI. The [final qualification](unattended-live-qualification.md#final-qualification-2026-11-17)
separates normal worker delivery, necessary system repairs, manual controller
repair X, automatic pilot release Y, and actual/synthetic browser observations.
This was an operator-assisted qualification, not an uninterrupted unattended run.
Core and Forge workload schemas are 41. TimeLoop and dedicated GitHub agent
identities remain separate follow-up projects.

The reports below preserve the original source audit, offline reproductions,
measured performance and dated intermediate outcomes. Historical findings are
not a list of defects still present in the qualified release.

Sprint-board qualification handoff:

- [Deployed sprint board and protected PR runbook](sprint-board-protected-pr-runbook.md)
- [Reusable evidence template](sprint-board-protected-pr-evidence.md)
- [Populated Sprint 4 operator receipt and current verdict](https://github.com/Xyrces/Forge/pull/159#issuecomment-6997942208) — the in-progress run is recorded separately from the template; follow this ledger for subsequent results and final outcome.

Reports:

- [PR #150 historical recovery, reviews and bounded timing](pr150-history.md)
- [Final UI-originated live qualification](unattended-live-qualification.md#final-qualification-2026-11-17)
- [Implemented bounded outcome planning](planner-design.md)
- [Routine deployment authority](routine-deployment.md)
- [UI journey and product direction](ui-journey.md)
- [Sprint planning and seven offline reproductions](sprint-planning.md)
- [Performance, query growth and refresh behavior](performance.md)
- [Usage accounting](budget.md)
- [Budget release compatibility](budget-release.md)
- [Completed live budget correction and release qualification](budget-live-qualification.md)
- [Completed live workflow repair qualification](workflow-live-qualification.md)

Original audit findings and directions (historical):

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

The initial read-only audit made no paid requests. Subsequent authorized pilot work is separately accounted for in the final qualification; usage estimates and the operator-reported $1.01 remain distinct from provider billing evidence.
