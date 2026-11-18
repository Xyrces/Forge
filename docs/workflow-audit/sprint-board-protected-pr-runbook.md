# Operator runbook: deployed sprint board and protected PR

Use this with the [evidence index](sprint-board-protected-pr-evidence.md). This
is a procedure, not authorization, deployment approval, or a test result. The
operator owns live execution and the verdict; reviewers and the hold placer act
as themselves. Workers may prepare the procedure and collate sourced receipts,
but cannot substitute their own checks for operator browser acceptance. Use one
authorized run and one live PR for the protected path. Do not replay PR #150.

## 1. Preflight before live work

1. **Confirm exact-revision authority.** Read the stored spec and version, not
   just its title, a design artifact, or an earlier approval. Obtain explicit
   operator authorization to execute that exact spec ID and revision in the
   named environment, project/repository, and window. Link the authorization
   (actor, UTC time, covered scope, and revision permalink or digest). Confirm
   the allowed test-only change, human actions, and negative-check sequence.
   Authorization for the docs or a release is not authorization for this run.
2. **Confirm the existing target and budget.** Record the existing K3s context,
   namespace and dashboard route, Azure SQL dataset/schema identity (no
   connection string), intended redesign release, full source SHA and build
   receipt. Read the existing all-projects budget snapshot (`GET /api/budget`
   where available): verify the **$50 global ceiling**, settled accounting,
   reservations (including uncertain requests), and remaining authorization
   for bounded worker, reviewer and rework activity. Agree a stop time and spending threshold within that headroom.
   Recheck before further paid activity and at handoff; ledger estimates are not
   provider invoices. Do not provision, migrate, deploy, roll back, or change the
   ceiling. If not already on the intended release, stop; this runbook cannot
   put it there.
3. **Verify existing role bindings without exercising writes.** Use the current
   controller/workload and project status, GitHub installation and collaborator
   records, Forge **Project overview → Pull request approvals**, and the branch
   rules in force for the target base. Record the serving controller identity,
   worker role/Job identity, controller's GitHub publisher, independent automated
   reviewer, human reviewer, hold placer and nominated human. Use non-secret
   IDs and cite proof for each role. Confirm the controller publishes/merges
   under protection with no bypass, the worker makes the testing change but
   does not publish, and the automated reviewer is distinct from the publisher.
   Confirm the reviewer is eligible to submit a blocking request and the
   nominee is the configured, eligible human and not the PR author. Confirm
   the hold placer has access to the chosen surface; record the dashboard role
   and GitHub login separately if both are used. A shared dashboard actor label
   alone does not identify the person; preserve the operator's attributed action
   record alongside the native event. Do not create, switch mappings, or grant
   permissions to identities. See [identity boundaries](../github-app-identities.md)
   for role reference, not setup instructions for this run.
4. **Plan the proof before dispatch.** Select exactly one hold surface (sprint
   card or GitHub `needs-human-review` label). Snapshot effective Forge review
   mode and nominee, branch protection/rulesets, required check names and
   publishers, and no-bypass status. Confirm the effective Forge merge stage
   gate permits the normal merge path once requirements are met. Name the
   read-only sources for the protected review decision, distinct Forge human
   gate, required CI and hold timeline through merge; confirm access to them
   and to evidence storage. Plan capture across the final approval so that an
   immediate merge does not lose gate and hold evidence. Agree how to keep any
   pre-correction head from merging and how to observe both negative cases.
   Do not race the watcher or rely on pending CI. If ordinary protection cannot
   support the sequence safely, do not start it.

### Read-only evidence sources

- **GitHub:** Use the live PR's Files changed, formal reviews, checks/statuses,
  and timeline, or their authorized API exports. Preserve the review's
  `commit_id`, submission time and state, the PR head and effective
  `reviewDecision` at evaluation, and the full check/run linkage separately.
  Read rule settings from an authorized view; a CI workflow file alone does not
  enumerate what protection requires.
- **Forge:** Scope all task reads to the project. The task detail read
  (`GET /api/tasks/{id}?projectId=…`) carries task metadata and events;
  `GET /api/tasks/{id}/human-review?projectId=…` reports the task hold, sync state,
  and nominee, not approval satisfaction. Use these and authorized review/hold
  audit records if available on the verified release. A snapshot that lacks
  evaluation time or attribution may need corroboration from a native trace;
  a truncated event list cannot prove continuous hold coverage.

### Plan the two negative checks

- **Earlier-head approval:** Use only a genuine approval by the otherwise
  eligible nominee on a legitimate earlier head of this same PR. To create it,
  first verify a separate normal blocker that cannot be satisfied by that
  approval and will keep the earlier head unmergeable until it is superseded.
  Do not approve a known-invalid diff or fabricate a review. If the authorized
  sequence cannot obtain such an approval on the initial head, use another
  legitimate predecessor only if already in scope and equally protected; record
  its full SHA separately. Confirm the effective reviewed SHA and decision at
  evaluation; if dismissal or a different blocker makes the head test
  inconclusive, record Blocked rather than inferring rejection by head.
- **Automated approval:** Use only a genuine approval from the configured
  reviewer through normal workflow. Observe it on the corrected head, so the
  actor type, not an outdated SHA, is what disqualifies it for human approval.
  An old bot review cannot isolate either condition. Never spoof events, replay
  reviews, impersonate an actor, change gates/rules, or dismiss a review to stage
  either case. No safe path or no independently attributable proof means Blocked.

## 2. Establish the serving revision independently

Before browser acceptance, follow the deployed URL's routing to the actual
serving workload using authorized read-only deployment records. For K3s, inspect
existing ingress/routing and Service, its EndpointSlices, and referenced ready
controller pods (for example `kubectl get` on those resources in the confirmed
context/namespace). Record UTC time, route/backend, active slot, pod names/UIDs,
controller container and actual runtime `imageID` for every serving endpoint.
Keep the routing/runtime export and independent release/build receipt as
separate sources. Compare the immutable digest to the receipt's `imageDigest`
and `sourceSha` (use a verifiable manifest mapping if the runtime reports a
platform digest). Confirm it is the intended full source revision; see the
[release receipt format](../../deploy/k3s/README.md#existing-deployment-approval-surface).
A desired image field, mutable tag, checkout, deployment health, or dashboard/API
revision label alone does not establish what served the route.

Record the window covered by each serving observation. Recheck at the end of
the browser window and whenever routing, replicas, or image change. If routing
is uncertain, replicas are mixed, or a rollout occurred during capture, stop
attribution until reverified. Recapture affected browser checkpoints against a
verified intended revision; do not stitch results across unverified revisions.

## 3. Independent browser acceptance

The operator observes the deployed `/sprints` page as the intended human role
with the authorized project and active sprint selected. Verify the actual CSS
viewport width in the browser, not the screenshot dimensions. Record width ×
height, browser/version, zoom/emulation and sidebar state. Wait for the live
board to load. For each checkpoint preserve an original timestamped capture or
recording, UTC time, role, route/project/sprint and relevant task/PR state,
interaction steps, expected and observed result, and the matching DEP record.
Consecutive captures may cover one checkpoint; one capture may cover several
if each is identifiable. Source review, local fixtures, APIs and CI are not
operator browser evidence.

- **1280 CSS px, sidebar open (D1):** Show the open sidebar and the sprint goal
  and board in separate vertical bands, goal above board, not side by side.
  Keep width and sidebar state unchanged if separate captures are needed.
- **1280 CSS px (D2–D6):** With the sidebar still open, use ordinary board
  navigation/scrolling to reach and use each lane. The current names to look
  for are **Queued**, **Running**, **Review**, **Needs you**, and **Completed /
  closed**; record actual headings and order. Read each heading, inspect a card
  or the empty state, and demonstrate permitted interactions where available
  (for example, open task details and return). All five need not fit in one
  image. A heading alone does not show that the lane content is usable.
- **390 CSS px (M1–M5):** Reach all five actual lanes from the board through
  normal UI paths (use the lane picker if shown). Record how each was reached
  and capture the heading and readable card content or empty state. Record
  clipping or obstruction, not just page overflow measurements.
- **390 CSS px (R1…Rn):** Inventory the actual review-related actions available
  to the role in the relevant states of the authorized sprint/PR. Capture exact
  control text, path from the board and, where safe, the prompt or destination.
  Follow a review link when it hands off to GitHub and show the handoff. Inspect
  the live states before placement, while held/reworking, and when review is
  due as applicable. Record absent or disabled actions with role/state and
  reason if known; do not count an obstructed available action as reachable.
  A control that writes immediately may be captured without invoking it. Do
  not add duplicate holds, release a hold, or submit extra reviews for coverage.
  The one authorized card placement may also be captured at 390 px; log it
  separately in MAN. GitHub review submissions do not replace board captures.

## 4. Observe the protected PR sequence

`H0` is the initial live test PR head and `H1` the corrected head. Replace those
labels with full SHAs in evidence. A release SHA, GitHub synthetic test-merge
SHA, or eventual merge commit is not either PR head.

1. **Testing-only PR and change request (PR-H0).** With preflight and serving
   proof complete and no unsafe merge window, let the authorized sprint worker
   prepare a small testing-artifact-only change and the controller publish it
   through the usual path. Link the active sprint/task, PR diff and full H0.
   The human reviewer files a genuine GitHub **Request changes** review with an
   inline finding on H0. Capture review ID, actor, time, reviewed SHA and thread
   permalink; capture GitHub's effective `CHANGES_REQUESTED` decision for H0 or
   equivalent native evidence that the formal review blocks merge there. A PR
   comment or generic non-mergeable state is not enough. Add the action to MAN.
2. **Mid-sprint hold (HOLD).** While the sprint is active and before accepting
   corrected-head review, place the hold once using the selected sprint card
   or `needs-human-review` label. Preserve the placement event, active-sprint
   evidence, actor, and the reconciled task hold/label and nominated-human
   current-head requirement. Record the MAN row. If synchronization is pending,
   wait for verified convergence without resubmitting; unverified state is not
   evidence that the hold is active.
3. **Same-PR rework and negatives (PR-H1 / GATE).** Let the worker address the
   inline finding through normal rework/publication. Capture the corrected diff,
   worker run/publication receipt and full H1 on the same PR. With the hold active
   and before the nominated H1 approval, capture the pending human requirement
   on H1, the automated H1 approval not counting, and the earlier-head approval
   not counting on H1. For each negative, pair the native review ID, actor/type,
   decision, review time and reviewed SHA with a timestamped, hold-specific
   evaluation at H1. Use an authorized Forge gate read or trace that ties the
   observed head, hold, nominee and human-pending result together. The existing
   task metadata can include `reviewProtectionHead`, `humanReviewTaskHold`,
   `humanReviewNominee` and `reviewHumanPending`; verify fresh attribution to
   each decision and rule out other explanations (such as an outstanding human
   objection). A stale board, overall merge block, or pending CI proves neither
   negative. Record automation and worker corrections outside MAN.
4. **Genuine H1 approval and required CI (GATE / CI).** When the review is valid
   and the negatives are captured, the nominee submits an actual GitHub approval
   of H1. Capture decision ID, inspected SHA, actor and timestamp, and verify
   the human gate is satisfied while the hold requirement/label is still in
   force. Record the nominee’s approval in MAN. Collect final H1 CI receipts
   before approval if the normal watcher could merge immediately; do not slow
   it down by modifying gates. In the separate CI ledger record every required
   check and status context, its trusted source/run ID, outcome,
   completion time and tested H1. If GitHub reports against a synthetic merge
   ref, require an authoritative linkage to the exact H1 tested in that run;
   a green badge or a run on a different head is insufficient. All required
   checks must have passed for H1 before merge. A newer head invalidates the
   H1 approvals and CI for this purpose; stop and reassess rather than silently
   reassigning H1.
5. **Normal protected merge (MERGE).** Observe the normal Forge/GitHub merge;
   do not merge by hand. Save the merge event (actor, UTC time, merged head,
   merge commit) and link it to the H1 gate and required-CI records. Keep
   contemporaneous hold-at-merge evidence plus GitHub PR timeline and Forge
   hold/audit records from placement through merge showing that the requirement
   or label stayed present and there was no manual clearing, label removal,
   review dismissal, owner override or other bypass. A post-merge label image
   alone cannot show continuity. No optional release is needed; if separately
   authorized and observed, record it as release, not approval or waiver.

## 5. Stop and hand off

Stop the affected execution before taking further actions if exact authority,
role eligibility, protected path, safe negative setup, serving identity, budget,
or credible evidence is missing. Stop further paid work if the agreed budget
threshold, headroom or window is exhausted; never increase the ceiling. If an
unexpected head, off-scope diff, unintended role/action, premature merge, or
bypass is observed, preserve the source and escalate, not repair around the
workflow. Leave holds and protections intact. A blocked check or failed run
is not authorization to retry, clear the hold, deploy, or make identity or
infrastructure changes.

Return the completed [run index and verdict](sprint-board-protected-pr-evidence.md#verdict)
with scope, participants, last safe checkpoint, unresolved items and durable
receipt links. Keep browser, deployed revision, CI and every manual intervention
independently inspectable. Report observed defects separately; do not backfill
missing evidence from implementation checks, design diagrams or prior runs.
Only the operator records acceptance, after checking the sources.
