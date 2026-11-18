# Evidence index: deployed sprint board and protected PR

**Live qualification: Pending operator observation.** This is the blank ledger
for the [operator runbook](sprint-board-protected-pr-runbook.md), not permission
to execute. Copy it per authorized run. Give each receipt a stable ID, record
UTC timestamps with timezone, and link the source (permalink, read-only export,
or original capture accessible to the next authorized operator). Record the
event time and collection time separately when they differ. Keep credentials,
session data and connection strings out of all records. Use full SHAs from the
sources; checkpoint names and example field names are not evidence.

## Run index

| Linkage | Value |
| --- | --- |
| Run ID, operator, authorized window and actual UTC start/end | Pending |
| Spec ID and exact approved revision; authorization receipt | Pending ([Preflight](#preflight)) |
| Target environment, K3s context/namespace, Azure SQL dataset, board URL | Pending ([Preflight](#preflight)) |
| Intended redesign release ID and full source SHA; build receipt | Pending ([Serving revision](#serving-revision-dep)) |
| Observed serving SHA and DEP observation window(s) | Pending ([Serving revision](#serving-revision-dep)) |
| Project, sprint ID/link, test task ID/link | Pending |
| Repository, protected base, live PR number/permalink | Pending ([PR and hold](#pr-and-hold)) |
| H0 initial head SHA; H1 corrected head SHA | Pending ([PR and hold](#pr-and-hold)) |
| Earlier reviewed SHA (identify if different from H0) | Pending ([Human gate](#human-gate)) |
| Merged head SHA and separate merge commit | Pending ([Merge](#protected-merge)) |
| Hold at merge; no-bypass coverage | Pending ([Merge](#protected-merge), [MAN](#manual-interventions-man)) |
| Last safe checkpoint, unresolved items, evidence location/access and receiving operator | Pending |
| Overall verdict, signing operator and UTC time | Pending ([Verdict](#verdict)) |

Procedure context: Sprint 4, `task-6` (runbook and index) and `client-3`
(protected PR/rework). This is not the observed sprint/PR linkage. Index those
from the live operator receipts above; reference the sibling's sourced run
records rather than starting a second run. [PR #150 history](#pr-150-history)
is separate. Worker build/fixture results and the documentation PR do not
satisfy any live row.

## Preflight

| ID | What to record | UTC, observed disposition and source |
| --- | --- | --- |
| P-AUTH | Stored spec ID/version/permalink or digest, explicit execution authorization (actor, time, exact revision, target, allowed changes/actions and time window) | Pending |
| P-TARGET | Existing K3s context/namespace and Azure SQL dataset/schema, deployed board URL, intended release ID/full source SHA and expected digest | Pending |
| P-BUDGET | $50 global ceiling, settled amount, outstanding/uncertain reservations, remaining headroom, estimated run cost, agreed stop threshold and time; snapshots before dispatch/further paid work and at handoff | Pending |
| P-POLICY | Existing review mode/nominee, protected base and required checks/status contexts with publishers, effective rules and bypass list, normal merge path | Pending |
| P-SEQUENCE | Selected hold surface and evidence sources; exact approved negative-check order, earlier-head blocker, how each negative and gate satisfaction can be observed safely, and evidence readers/storage | Pending |

| Existing role | Non-secret identity, repository/project scope, time and role/eligibility proof |
| --- | --- |
| Operator / browser observer and intended dashboard role | Pending |
| Controller workload / service identity | Pending |
| Controller GitHub publisher (App/login and installation ID where applicable) | Pending |
| Engineering worker role / Job identity | Pending |
| Independent automated reviewer (App/login and installation ID where applicable) | Pending |
| Human change-request reviewer | Pending |
| Human hold placer for chosen card or label surface | Pending |
| Configured eligible nominated human approver | Pending |

## Serving revision (DEP)

Add a row per independently verified serving observation (before browser,
after, and when routing/image changes). In the routing/runtime receipt identify
the target and every serving endpoint/pod UID, controller container, and
immutable running image identity. In the separate release receipt identify
build workflow/run, digest and full source SHA; record the match to P-TARGET and
any digest mapping proof. Never substitute the dashboard revision field.

| ID | Observation UTC / covered window | Route/target, serving workload and runtime digest/source | Independent release/build receipt and serving source SHA | Match or gap |
| --- | --- | --- | --- | --- |
| DEP-1 | Pending | Pending | Pending | Pending |

## Browser observations (UI)

Use [the capture instructions](sprint-board-protected-pr-runbook.md#3-independent-browser-acceptance).
For each row enter capture ID/link and UTC time; DEP ID; actual view settings
(CSS width × height, browser/version, zoom/emulation, sidebar state); operator
role; route and project/sprint, task and PR state where relevant; steps and
observed result. Use the deployed lane and control names. Add one row per
review action/state as needed, including disabled/absent cases with context.

| Checkpoint | Expected evidence | Actual steps, result and capture metadata/source |
| --- | --- | --- |
| D1 | 1280 CSS px, sidebar open, goal above board | Pending |
| D2 | Desktop lane 1: heading and usable content/empty state | Pending |
| D3 | Desktop lane 2: heading and usable content/empty state | Pending |
| D4 | Desktop lane 3: heading and usable content/empty state | Pending |
| D5 | Desktop lane 4: heading and usable content/empty state | Pending |
| D6 | Desktop lane 5: heading and usable content/empty state | Pending |
| M1 | 390 CSS px, lane 1 reached and readable | Pending |
| M2 | 390 CSS px, lane 2 reached and readable | Pending |
| M3 | 390 CSS px, lane 3 reached and readable | Pending |
| M4 | 390 CSS px, lane 4 reached and readable | Pending |
| M5 | 390 CSS px, lane 5 reached and readable | Pending |
| R1…Rn | 390 CSS px, each available review action reachable through the normal UI | Pending |

## PR and hold

| ID | Required source and linkage | UTC observations and source |
| --- | --- | --- |
| PR-H0 | Active sprint/task, controller publication receipt, testing-only diff, live PR and H0 | Pending |
| PR-H0-REQUEST | Native blocking review decision on H0, review ID, reviewer, reviewed SHA, genuine inline finding, link to MAN | Pending |
| HOLD | Active-sprint evidence, placement surface/event, reconciled hold/label, nominee and current-head requirement, link to MAN | Pending |
| PR-H1 | Worker correction on same PR, diff addressing finding, run/publication event, H1, link to AUTO-WORKER | Pending |

## Human gate

For each applicable row include PR and sprint, evaluation UTC, current SHA,
review ID and native link, review actor/type, decision, submitted UTC and
reviewed SHA, hold/label state, and distinct human-gate outcome with source.
For each negative, record how the source established the reason after the
review was evaluated, not just that the PR had not merged. Link human actions
to MAN and automated actions to AUTO-REVIEW.

| ID | Expected observation | Review and independent gate evidence |
| --- | --- | --- |
| G-PENDING | Hold active; required nominated-human approval missing on H1 | Pending |
| G-AUTO | Recorded automated approval on H1 did not satisfy human gate | Pending |
| G-STALE | Genuine approval on earlier reviewed SHA did not satisfy human gate on H1 | Pending |
| G-APPROVED | Nominated human approved H1; human gate satisfied with hold still present | Pending |

## Required CI by head (CI)

Add a row per required check/status from P-POLICY, and link the exact set of
passing H1 records to MERGE. Keep any H0 observations separately identified;
never count them, a main build, or deployment build receipts toward H1.

| ID | PR, required check/context and trusted publisher | Run/check/status ID and source | Tested full head SHA (and exact-head linkage if GitHub reports a synthetic ref) | Outcome and completion UTC |
| --- | --- | --- | --- | --- |
| CI-1…n | Pending | Pending | Pending | Pending |

## Manual interventions (MAN)

Log each actual manual intervention individually, including preflight actions
that changed workflow state within scope, if any. Add rows as needed; do not
assume there were no additional actions from a short ledger. A release, if
separately observed, is a release action and does not waive a review. Keep
worker and reviewer automation out of this ledger.

| ID / action expected | Actor and role | UTC event time | Action performed and surface | Sprint / PR and reviewed/current SHA where applicable | Source link and related IDs |
| --- | --- | --- | --- | --- | --- |
| MAN-REQUEST (inline request) | Pending | Pending | Pending | Pending | Pending |
| MAN-HOLD (placement) | Pending | Pending | Pending | Pending | Pending |
| MAN-STALE (earlier approval if arranged) | Pending | Pending | Pending | Pending | Pending |
| MAN-APPROVAL (nominee H1 approval) | Pending | Pending | Pending | Pending | Pending |

## Worker and automation

| ID | Actor/service, run/event ID, UTC, PR and exact SHA | Outcome and source |
| --- | --- | --- |
| AUTO-WORKER (worker correction) | Pending | Pending |
| AUTO-REVIEW (automated review) | Pending | Pending |

## Protected merge

| ID | Required linkage | Observation and source |
| --- | --- | --- |
| MERGE | Normal merge event, actor, UTC, sprint and PR, merged H1 and merge commit; link G-APPROVED and all required CI IDs | Pending |
| MERGE-HOLD | Hold/label present at merge; contemporaneous state and timeline/audit covering placement through merge, with HOLD and MAN IDs | Pending |
| MERGE-PATH | Protection and timeline/audit proof of no hold clearing, label removal, review dismissal, owner override or other bypass; flag coverage gaps | Pending |

## Verdict

Pending means not yet observed, not a completed determination. On closure use
**Pass** only for demonstrated criteria, **Fail** for a sourced contradiction,
and **Blocked** when missing authority, safety, evidence or budget prevents a
determination. Overall Pass requires all applicable criteria; report observed
failures even if other criteria are blocked. Record operator and UTC time in
the run index and cite exact receipt IDs and any separate defect IDs.

| Criterion | Evidence series to cite | Verdict and reason |
| --- | --- | --- |
| Exact-revision authorization, roles and budget | Preflight | Pending |
| Intended redesign independently verified as serving | DEP | Pending |
| 1280 px goal/board stacking and five usable lanes | D1–D6 + DEP | Pending |
| 390 px five reachable lanes and available review actions | M1–M5, R1…Rn + DEP | Pending |
| Protected rework and active-sprint hold | PR-H0, HOLD, PR-H1 + MAN | Pending |
| Automated and earlier-head approvals rejected for the human gate | G-AUTO, G-STALE + AUTO-REVIEW/MAN | Pending |
| Nominee approved current H1; required CI passed on H1 | G-APPROVED + MAN + CI | Pending |
| Protected merge on H1 with hold in place and no bypass | MERGE, MERGE-HOLD, MERGE-PATH | Pending |
| All manual actions accounted for and evidence independently reviewable | MAN + related sources | Pending |

## Defects (separate from verdict)

Pending observations. For each observed implementation defect, record a defect
ID/link, expected vs observed, repro steps, route/role/state, viewport where
applicable, serving SHA and PR head when relevant, UTC, source IDs and impact.
Keep source gaps or unsafe prerequisites as blockers, not presumed defects.

## PR #150 history

Read-only historical context for [PR #150](https://github.com/Xyrces/Forge/pull/150),
not live PR or native-review proof. The retained
[history fixture](../../scripts/browser/fixtures/pr150-history.json) transcribes
legacy reviewer comments and verified commit links. The
[redesign audit](task-review-ux-plan.md) records the Forge recovery account.

| UTC on 2026-11-18 | Historical event / associated revision | Source |
| --- | --- | --- |
| 01:35:15 | Legacy reviewer comment (`Xyrces`), Approve at `66ca85f89c1510cf8f31045ec381e1d81707d441` | [Round 1](https://github.com/Xyrces/Forge/pull/150#issuecomment-6966317332) |
| 01:55:21 | Legacy reviewer comment (`Xyrces`), RequestChanges at `190d128b59c88c1ff684ffb5a2c941c65032888d`; finding concerned an unrelated orchestration-test change | [Round 2](https://github.com/Xyrces/Forge/pull/150#issuecomment-6966646881) |
| Timestamp not established here | Repeated observations consumed attempts; publication stopped and one operator requeue recovered it. Exact requeue actor/time and wait boundaries are not established by this audit summary. | [Forge recovery account](task-review-ux-plan.md) |
| 02:16:37 | Legacy reviewer comment (`Xyrces`), Approve at `263bbfeb44a12565e3738b01ae08bbc92283ae63`; CI was still pending per comment | [Round 3](https://github.com/Xyrces/Forge/pull/150#issuecomment-6966990156) |
| 02:20:07 (commit timestamp) | Merge commit `30a6ec9f8b740dd4bd2775b26edac0db7ee48997` has final head `263bbfeb44a12565e3738b01ae08bbc92283ae63` as second parent; commit timestamp is not a PR timeline export | [Merge object](https://github.com/Xyrces/Forge/commit/30a6ec9f8b740dd4bd2775b26edac0db7ee48997) |

The two final reviewer-comment timestamps bound an elapsed **21m 16s** from
request comment to approval comment, not pure worker, verification or human
wait time. The [historical UI/CI report](sprint-ui-qualification.md#production-reference-from-november-18-2026)
indexes prior CI, packaging and rollout intervals and points to retained
receipts under `timing/pr150`. Do not reconstruct any additional phase duration
without timestamped sources for both boundaries. If adding history, link the
actual GitHub timeline item and/or Forge recovery receipt, label source gaps or
conflicts, and never replay or mutate this PR.
