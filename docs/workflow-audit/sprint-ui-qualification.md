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

## Readable specifications and automatic-stage explanations

The live pilot's refined specification exposed raw Markdown and a generic
"no available actions" explanation while waiting for automatic design.
Source through `b4ceb8d` replaces the raw text block with the existing
HTML-disabled Markdown renderer and explains the next stage, including the
effect of a hold, with a link to the Flow controls. Action eligibility is
unchanged.

The actual long specification body was copied into an isolated, explicitly
labeled offline fixture. Browser checks at1280×900 and480×850 showed readable
headings, paragraphs, acceptance criteria and the automatic-stage explanation;
the narrow layout kept the content within the viewport. Screenshots
`readable-spec-desktop.jpg` and `readable-spec-narrow.jpg` are retained in
the private preview directory. The Mermaid block remains readable fenced
source; this check does not claim an interactive diagram renderer. No
scheduler, provider call or production data mutation occurred in the preview.

## Sprint and task review redesign

The [accepted design](task-review-ux-plan.md) calls for an active sprint’s goal
and acceptance progress above its five card lanes, readable task and review
history, and human review during an ongoing sprint.

### Production reference from November 18, 2026

At 05:01 UTC Linux Chrome opened Sprint 3 and the task for PR #150 on production
revision `30a6ec9f8b740dd4bd2775b26edac0db7ee48997`. At a viewport width of
1280 pixels, all three completed cards began at x=1131 and ended at x=1309.
The document width was 1280, which hid 29 pixels of clipping from an overflow
check that measured only the page. At a width of 390 pixels those cards spanned
x=23 to x=367. All three historical sprints had finished before this capture.

The task title, PR link and original review text appeared after 1.98 seconds
on desktop and 1.02 seconds on mobile. Each number is a single navigation
sample. The screenshots and input measurements are in the Linux sprint-review
directory under `baseline-pr150-stable/receipt.json`.

| PR #150 delivery interval | Duration | Source |
| --- | ---: | --- |
| CI on the final PR revision `263bbfe` | 186 s | GitHub check run |
| CI on merged revision `30a6ec9` | 194 s | GitHub check run |
| Image packaging | 257 s | GitHub Actions run |
| Image workflow completion to rollout Job start | 163 s | Actions completion and K3s Job start |
| K3s rollout Job | 80 s | Job start and completion |

Main CI and image packaging overlapped. The retained release receipt links the
deployed digest to the source revision. The old controller did not record
separate dispatch eligibility, model/tool work, verification or human wait
boundaries for this task. The Linux `timing/pr150` directory contains the
source records used by the read-only collector.

### Linux browser evidence

The following receipts are under `task-browser-20261117` in the Linux
qualification directory. They exercise real Blazor components against a
loopback fixture API, without schedulers or paid model calls.

| Receipt | What the browser observed |
| --- | --- |
| `browser-qualified/receipt.json` | The goal, acceptance criteria and five lanes render in one desktop flow with the sidebar open. Card bounds stay inside each lane. On mobile, all five lanes and their actions are reachable. A hold during rework leaves the card in Running. |
| `browser-qualified-runs/receipt.json` | The run title and metadata fit a 390-pixel viewport. The test checks the document width after the exact run has loaded. |
| `browser-task-performance-final/receipt.json` | Task detail fetches the task record once across prerender and hydration. It waits until History & runs opens to fetch activity. The PR #150 fixture includes the four criteria assigned to this task, the verified revision `263bbfe` and observed merge `30a6ec9`. Browser acceptance remains separate. |
| `browser-nomination-final/receipt.json` | Project policy saves a nominated human. A running card offers the action by name, and the nominated person becomes the next actor when review is ready. The name belongs to this fixture. |
| `browser-native-history/receipt.json` | A human requests changes inline, another review approves the corrected revision, and both records remain visible with their original authors and links. The view distinguishes advisory comments, dismissed decisions and older Forge comments. Long paths wrap at 390 pixels. |
| `browser-review-prose-final/receipt.json` | The PR #150 view shows the historical reviewer's findings without protocol markers. Human-authored text stays verbatim. Seven-character revision links expose the full commit hash and work in both review and delivery evidence. |
| `browser-navigation/navigation.json` | Six journeys exercise browser Back, app links, keyboard focus, scroll position, lane and sprint selection, paged task searches, and separate project histories. |

The component runner captures populated content after hydration and checks the
visible text again after saving each screenshot. This replaced an earlier
`browser-final` capture that showed a loading screen. Separate source and SQL
tests cover existing-code verification, cancellation, unauthorized changes,
review gating, and persisted hold behavior. See the
[SQL qualification record](review-sql-qualification.md).

### Browser check with the actual handlers and stores

`browser-real-handlers/receipt.json` covers the actual DashboardHost with
isolated task, sprint and worker-attempt stores, operator authentication, and
synthetic GitHub read responses. A card action persisted a human-review
requirement. The task stayed Running with its worker and showed the
configured reviewer, the anchored finding and its stored history on both
desktop and mobile. Reopening the stores confirmed the saved requirement
and running worker. An unauthenticated write returned HTTP 401.

This run exposed two hosting issues. Task and history routes now register
independently of the optional codebase service. A host with a configured
dashboard port and no explicit Kestrel endpoint now binds to that port.
An integration test starts a real host and checks those routes.

A protected PR with installed production Apps, an actual human decision, and
browser observations of the deployed revision are still required for live
acceptance.
