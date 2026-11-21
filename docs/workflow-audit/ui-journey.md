# Live UI journey audit

Reviewed November 17, 2026 through the existing loopback dashboard tunnel. Live
source is `1b733cb`; inspected source baseline is `8460dde`. This was a read-only
browser walkthrough of Now, Intake, Specs, Sprints, Backlog, Flow, Deployments and
Gates, plus source tracing. No new intake, model call, gate change, task or
deployment was submitted. It is **not** end-to-end acceptance evidence.

## Confirmed user-visible defects

| Finding | Observed evidence | Consequence / proposed correction |
|---|---|---|
| Deployment history cannot render | Deployments shows `The JSON value could not be converted to ...DeploymentRow. Path: $[0].status`, followed by `No deployment candidates yet` | API returns an enum as a JSON number; UI expects a string. Use one tested contract. A failed load must never claim an empty history. |
| Finished work is called in-flight | Flow says `2 ITEMS IN FLIGHT`; both items are in `Merged / done` | Count active work separately. |
| Cancellation is conflated with delivery | The done node includes completed `dev-1` and closed `task-1` | Distinguish merged, verified no-change, cancelled/superseded and deployed. Closing work does not prove its requirement delivered. |
| PR evidence links are malformed | Sprints links `https://github.com/Xyrces/Forge.git/pull/124` | Normalize clone URLs once through a shared repository-link helper. |
| Task entry promises the wrong next step | Backlog → New task says `the dispatch loop claims it next cycle` | Explain grooming, sprint selection and any active hold; route new product work to intake. |
| Gate state briefly claims open before loading | Initial Sprints render shows all four open; after retrieval all four become held | Unknown/loading must not default to open. |
| “Gates” has two different meanings | Now contains design/groom/sprint/merge pause controls; sidebar Gates opens schema/territory/LLM plan checks | Clearly separate workflow pauses from quality checks and link to the correct surface. |
| Task history is hard to find | Tasks shows only `0 IN PROGRESS`; completed work is available under Sprints/Backlog | Rename to Active tasks or expose obvious status filters and links. |
| Empty specification list gives no next step | Specs shows an empty table with filters, while Intake has no sessions | Provide a clear start-work action and explain when a specification appears. |

The four attention items on Now are the four deliberate stage holds, **not**
historical failures. The header's `healthy` label reflects service health; it does
not establish that work can progress. Now does show the holds, which is useful.
The proposed overview should make service availability, paused automation,
budget availability and work blockers distinct rather than overloading one badge.

Source anchors: `Forge.UI/Features/Deployments/DeploymentsState.cs:27` and
`Dashboard/DeploymentsEndpoints.cs:48` (deployment contract),
`Forge.UI/Components/Pages/Flow.razor:16` (in-flight count),
`Dashboard/FlowEndpoints.cs:329` (done projection),
`Forge.UI/Components/Pages/Backlog.razor:54` (task promise),
`Forge.UI/Components/Pages/Sprints.razor:30` and `:342` (gate default/load),
`Sprints.razor:581` and `:604` (PR link), `Flow.razor:326` (repository selection).
TaskDetail, SpecDetail and Backlog duplicate the same URL construction. Flow
also selects the first registered repository instead of the current project;
this is a source-confirmed multi-project risk, not reproduced on the one-project
live dataset.

## Unattended delivery is not the current product contract

Deployments explicitly says an operator chooses a commit and approves the build
result. Its backend creates candidates only on an explicit request. Intake also
has an accept-proposal handoff. These are real policy choices, not merely slow UI.
The current four stage gates are held deliberately for qualification. Simply
releasing them would not establish an unattended intake-to-deployment loop.

Recommended direction for agreement:

1. A single request journey with plain-language current stage, next automatic
   action, blocker/decision, evidence and deployed result. Keep detailed screens
   reachable as drill-downs.
2. A project-level autonomy policy: acceptance criteria and scope agreed once;
   automatic planning, implementation, review and qualified deployment within
   that scope. Escalate ambiguous scope, failed verification, exhausted retry or
   spending authority, and incompatible deployment instead of requiring routine
   clicks. Preserve deterministic quality and publication checks.
3. Outcome-based sprint planning with bounded scope, prerequisite closure and
   related remediation/backlog selection; show why each item is included or
   deferred. Use one validator for every activation path.
4. Honest states: paused, blocked, running, merged awaiting deployment, deployed,
   cancelled and partial outcome. Do not silently age failures into success.
5. Bounded page reads and consistent loading/error/stale states (see performance
   report). Do not hide data failures behind empty lists.

## Acceptance that would establish the intended result

Start with a request entered through the UI. Follow its real intake, approved
scope/specification, technical grooming, sprint proposal, worker execution,
review, merge, release and visible result. Do not seed an intermediate task or
sprint and call that a UI test. Exercise a related remediation plus backlog case,
a blocked prerequisite, a controller restart, and a failed release rollback.
Record every operator intervention; a successful API response alone is not UI
acceptance. Use offline fakes and disposable datasets for most fault/scale tests,
then one bounded paid live journey under the existing $25 authorization.

The operator subsequently clarified initiative authority: they introduce work
through intake; Forge may suggest initiatives as drafts, but those suggestions
require an intake conversation and approval before ingestion. Within approved
scope, planning and execution should be autonomous. The detailed planner design
records remaining policy defaults separately from the confirmed correctness fixes.

## Spending UI qualification

The new spending component was inspected in the actual dashboard using an offline
loopback test host and a typed read-only budget fixture. It showed $25 authorization,
$1.01 operator-reported prior cost, zero outstanding holds and $23.99 available.
Clicking Refresh spending while the fixture failed retained the previous values and
displayed a stale-data warning; restoring the fixture and refreshing removed the
warning and advanced the timestamp. This verifies display and refresh behavior,
not production reconciliation. Endpoint tests separately cover missing fields,
missing datasets, unknown liabilities and failure responses. No paid model call or
production mutation was part of this UI check.
