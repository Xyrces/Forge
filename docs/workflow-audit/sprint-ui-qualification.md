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


## Task, review and sprint presentation rehearsal (November 17)

The actual Blazor components were built and hosted on the Linux machine in
`~/forge-revival-validation/task-browser-20261117`, independently of the live
service. A loopback-only fixture API supplies server-side HttpClient responses;
there is no production database, scheduler or model call in this rehearsal.
The build passed with zero warnings and errors. The reusable runner is
`scripts/browser/task-review.spec.mjs`; its board assertions are shared with
`scripts/browser/sprint-board.spec.mjs`.

`browser-final/receipt.json` records a passing desktop1280 and phone390 run:

- Five desktop lanes occupy the same row with the sidebar open; every lane
  and card fits, including the rightmost Completed / closed lane. Every phone
  lane is selectable, fits, and retains selection after refresh.
- A human-review hold added during active work stays in Running and records
  one successful hold write. It does not imply the worker stopped.
- PR150's three historical comment decisions use verified original comment
  URLs and exact repository excerpts from190d128 and263bbfe. Historical
  comment provenance stays distinct from formal reviews. An earlier note's
  pending checks are not silently rewritten as passed.
- Task overview, review selection/source toggles, structured plan checks and
  timeline render at both widths. An unreadable critic verdict is shown as
  allowed with warning, distinct from the two passed deterministic checks.
- Separate explicitly synthetic fixtures exercise active rework, completed
  work lacking merge evidence, linked agent-run detail, measured eligibility,
  model/verification and human wait timings, and independent gate/publication
  integration errors. These fixtures do not assert historical PR150 run data.

Screenshots in `browser-final` include visible desktop cards, each phone lane,
PR150 review/plan/history, and synthetic worker/run views. Desktop/mobile card
screenshots were visually inspected. No browser page errors occurred; aborted
Blazor disconnect requests on deliberate navigation are recorded in the receipt.
This is an offline presentation qualification, not proof of a live merge,
deployment, native GitHub review publication or current release acceptance.

Browser testing exposed unserved isolated board styles. The three components'
styles now ship in the linked shared `sprint-review.css`; the successful rerun
confirms the delivered styles, rather than merely inspecting their source.


### Capture audit correction

The earlier `browser-final` screenshots are superseded: the parent review
caught an overview captured during Blazor rehydration, despite earlier logical
assertions passing. The runner now waits for populated, stable content on each
capture, compares exact DOM text before and after it, and retains a `.png.txt`
companion. Run navigation additionally waits for its exact URL and run-only
content. Assertions cannot substitute for inspecting the captured image.

`browser-qualified/receipt.json` is the passing full rerun. All22 screenshots
were visually audited using the audit contact sheets. Finding/check/timeline
and worker sections are scrolled into view because the dashboard uses an inner
scrolling surface; `fullPage` alone does not show their offscreen content.
The active sprint now has explicit synthetic engineering/browser criteria,
merged and pending work, and separate pending deployment/observation states.
The duplicate leading Goal heading is suppressed under Sprint objective.

That visual audit found RunDetail's narrow header overflow. Its title now uses
its own row and metadata wraps; `browser-qualified-runs/receipt.json` records
the subsequent task/run regression check including a document-width assertion.
These screenshots precede any later task-overview acceptance-component changes;
that new surface requires its own qualification.


### Task detail loading and authoritative delivery summary

A subsequent Linux-only task qualification uses the actual allowlisted PR150
metadata, fetched read-only from the existing service. It includes its four
assigned acceptance criteria (one implementation and three operator observation
requirements), excludes the sibling's regression criterion, and preserves the
recorded263bbfe verification and30a6ec9 merge/time. Operator observations are
shown as requirements, never inferred as passed from implementation evidence.

The `browser-task-performance-final` receipt records one task overview API read per
navigation, including prerender-to-interactive handoff. A persisted component
snapshot eliminates duplicate hydration reads. Overview fetches no task history,
worker attempts, run collection or legacy source history. History opens those
reads once, concurrently, with task-scoped runs and cancellation/generation
checks. Navigation resets prior evidence and selection. Refresh invalidates
activity/review caches. The additive includeEvents=false contract avoids reading
100events only to fetch the dedicated history endpoint later; the default API
contract remains compatible.

The focused Linux run passed79 tests: task evidence (including capturedPR150,
independently reviewed existing code, and historical cancellation), task endpoint
contracts/history, active-run scoping, task situations and delayed response/navigation authorization. Browser checks cover
hydrated1280/390 presentation and request counts. Build passed without warnings.
The test project retains unrelated existing analyzer warnings.

The reusable runner now resolves Playwright normally or through the documented
FORGE_BROWSER_NODE_PACKAGE override; an optional FORGE_BROWSER_EXECUTABLE selects
installed Chrome. Host-private paths are supplied only by the invocation.


### Nominated human reviewer qualification

`browser-nomination-final/receipt.json` records an offline actual-component
walkthrough: save the optional login from Project overview, activate the board's
Require review by @Xyrces action during running engineering, and view the
configured person as task next actor only for an actual human-review wait.
The existing desktop/sidebar and mobile lane bounds checks run in this fixture.
The name is fixture configuration only; production has no hardcoded nominee and
no production setting was changed. Project and mobile-task captures were
visually inspected.

46 focused Linux tests cover the nominated eligible human's current-head
approval, another writer's non-substituting approval, case-insensitive matching,
stale approval, PR-author/bot exclusion, every eligible human's outstanding
objection, project scoping, audited/authenticated save, optional-field preservation
and explicit clearing. Watcher integration exercises Both and additional-hold
paths, and clearing restores eligible-human fallback without changing Human mode.

## Production reference for the sprint and task redesign

On November 18 at 05:01 UTC, Linux Chrome opened PR #150's actual Forge task
and Sprint 3 on production revision `30a6ec9f8b740dd4bd2775b26edac0db7ee48997`.
At a 1280-pixel viewport each of the three completed cards started at x=1131
and ended at x=1309. The outer page measured 1280 pixels wide, so checking
document overflow alone missed 29 pixels of clipping. At 390 pixels those
historical cards spanned x=23 to x=367. The saved screenshots show the same
task and sprint at both widths. All prior sprints were complete at capture.

The reference read is recorded in `baseline-pr150-stable/receipt.json` under
the Linux sprint-review qualification directory. Its task-ready samples were
1.98 seconds at desktop width and 1.02 seconds at phone width, measured from
navigation to the visible task title, PR link and recorded review text. These
are individual browser observations. The pilot must separately measure the
redesigned UI, human review, CI and deployment. The
[accepted design](task-review-ux-plan.md) defines the operator journey.

### Mixed native review history

The lazy task Review view reads up to 100 formal GitHub reviews and 100 inline comments, preserving actual authors, recorded revisions, discussion permalinks and original diff context. It retains legacy ordinary comments alongside native reviews and deduplicates cached Forge review records by publication URL. Historical requests, dismissed reviews and advisory comments are explicitly distinguished from a new merge decision. A later approval is not presented as proof that each discussion was resolved. No worker correction is inferred without a separate receipt.

Linux offline runner `scripts/browser/native-review-history.spec.mjs` qualifies a synthetic App review, human requested changes with inline discussion, later human approval, advisory/dismissed decisions and a legacy comment. Receipt: `browser-native-history/receipt.json`; hydrated screenshots include the native decision summary and expanded inline discussion. Fifteen focused tests passed (native history, navigation guards and local GitHub-service overrides). These receipts do not establish production behavior or make a GitHub mutation.
