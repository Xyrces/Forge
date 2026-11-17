# Sprint UI qualification

Browser qualification on 2026-11-17 used the actual integrated DashboardHost and Blazor components on loopback port 5011, with synthetic project stores and no schedulers or model calls. Alpha contained 53 historical sprints and 235 active members; Beta contained four histories and 12 active members. This was targeted UI qualification, not a real intake-to-deployment journey.

Observed through the browser:

- Alpha history paged through 20, 20 and 13 records, keeping the active sprint available. The oldest page disabled Older sprints.
- Active task pages contained 100, 100 and 35 members. Whole-sprint progress remained 74 of 235, while column counts described only the selected page. The final task page disabled Next tasks.
- Selecting historical sprint 41 showed its one completed member and read-only state, with no current dispatch queue mixed into that history.
- A delayed Alpha refresh followed immediately by selecting Beta left Beta's 12-member board visible after the late-response window; Alpha did not overwrite it.
- A failed Alpha refresh retained the last good board, displayed a warning and last-success timestamp, and recovered after normal responses resumed.
- Empty Specs displayed a Start in Intake link; following it opened the actual Intake page. No session was created or model request submitted.

Browser testing found two remaining presentation defects and a project-switch rendering defect. They were corrected before release:

- Initial loading had briefly claimed no sprints existed. `c4afd98` limits the empty message to a confirmed board response. A fresh browser tab subsequently showed Waiting for a project selection, with totals and gate status still unknown.
- Closed and Completed shared a Done column. `c4afd98` labels the column Completed / closed and states that progress counts tasks completed or closed; it does not claim cancelled work was delivered. The corrected text was observed in the browser.
- During a slow project switch, the old board remained rendered under the new project selection until requests completed. `207b1d3` renders the cleared loading state immediately and fences asynchronous repository-link lookup by the same selection generation/cancellation token. With delayed Alpha responses, switching from Beta immediately removed Beta's board and showed Loading with unknown totals. Repository-link stale-response protection was also source reviewed and compiled.

The final fixture used application source through `207b1d3`; the next commit only records SQL qualification. Earlier paging/error checks used the same read/query logic. Actual Azure SQL read and activation transaction checks are recorded separately in [the read model report](sprint-read-model.md) and [activation safety report](activation-safety.md).

One fixture restart coincided with navigation and left a temporary browser connection-error page. After the fixture returned healthy, a fresh tab was used for the remaining checks. This was a test-host restart, not a production outage or a passed application recovery test. Test controls were reset and the private test host was stopped after qualification.

## Completion evidence and larger history fixture

The unattended-workflow preview through `5a4b61d` contained130 synthetic
sprints and25 outstanding completion records, with no scheduler or provider
credentials. History loaded20 summaries per page while preserving the
selected sprint. The separate outstanding list loaded20 records followed by5.
Selecting a historical sprint with no receipt removed the previous evidence;
using an outstanding-record Review control selected its exact sprint even
when that sprint was outside the current history page.

A synthetic deployed completion with all tasks completed still displayed
awaiting operator observation. Its interrupted follow-up batch exposed an
explicit retry action; clicking it changed NeedsReview to Pending and
confirmed the request without claiming delivery or making a model call.
The private screenshot `completion-retry-desktop.jpg` is retained under
`.portHorizon/validation/intake-preview/` in that integration checkout.

The larger fixture also exposed an overly tall outstanding list and raw
status names. The corrected overview loads five items and starts collapsed.
Browser rechecks through `cd68279` confirmed readable sprint names and that
an uncertain batch is labeled "Review needed before retry" consistently in
both the overview and selected detail. Selecting its Review button opened
the exact matching sprint and retained its explicit retry warning.

Browser checks through `d35f343` also exercised an explicit stop: the button
remained disabled without a reason, and submitting a reason preserved the
unmet criterion and healthy synthetic release while stating "not delivered"
and "Stopped by operator". No retry action remained after stopping. At a
480-pixel viewport the stopped outcome remained readable. The Flow page's
deploy hold/release control changed its displayed gate state through the
normal authenticated endpoint; the offline gate was restored open.

Screenshots `completion-review-summary.jpg`, `completion-stopped-narrow.jpg`
and `deploy-gate-narrow.jpg` are retained beside the earlier evidence. This
fixture is presentation and recovery interaction evidence only, not a real
release or acceptance result. It has no schedulers or paid provider access.

## Technical planning admission and interrupted recovery

The same offline DashboardHost fixture through `33d5613` exercised the normal
Groom action and the explicit retry of an interrupted grooming run. Normal
admission immediately displayed "Technical planning: Queued", removed the
duplicate action, and stated that queue admission is not completed planning.
The existing zero-story decomposition remained unchanged until execution.

The interrupted fixture retained its existing pending story and task after
retry. Reloading showed the queued retry and the earlier interrupted attempt
in history, with a readable explicit-retry label. Browser testing exposed a
contradictory "no available actions" explanation and raw status names; the
recheck confirmed the corrected explanation and history labels. Screenshots
`grooming-normal-queued.jpg` and `grooming-retry-queued.jpg` are retained in
the private fixture directory. No scheduler or model ran in this UI fixture;
actual SQL concurrency and recovery are covered by the separate storage report.
