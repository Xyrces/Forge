# PR #150: historical recovery, reviews and timing

This is a read-only reconstruction of [PR #150](https://github.com/Xyrces/Forge/pull/150), task `client-2`, Sprint 3 (`sprint-6b1f419a15`). It is not the live Sprint 4 protected-review qualification and does not authorize a replay, release, review, or recovery action. Codex assembled this report from GitHub, retained Forge state and Git objects; no Forge worker or paid model call was dispatched for it.

The original reconstruction used documentation base `86bcec3`; this report was rebased onto `e16ba9323e5c2b2434fe9efeb64eb6100f7234b0` without changing the collected source snapshots. Forge evidence was collected on **2026-11-19 at 02:18:02 UTC**, after the historical events. Event times below are November 18, UTC. Forge's event API emits timestamps without an offset; they are presented using the store's UTC convention, while the original strings remain in the snapshot.

For the separate in-progress Sprint 4 qualification, use the [populated PR #159 operator receipt and current verdict](https://github.com/Xyrces/Forge/pull/159#issuecomment-6997942208). Its subsequent observations and final outcome belong in that ledger, not this historical report.

## Sources and verification

| Source | What it establishes | Limits |
| --- | --- | --- |
| [GitHub PR and retained response](pr150-github-evidence.json) | PR creation/merge timestamps, final head, merge commit, three original reviewer comments with actors and timestamps | The retained comments are issue comments, not native review submissions or inline review threads. |
| [Forge task and completion snapshot](pr150-history-evidence.json) | `GET /api/tasks/client-2?projectId=forge` and `GET /api/sprints/sprint-6b1f419a15/completion?projectId=forge`; selected task fields, events and completion criteria | Read-only snapshot of the existing Linux service, not a fresh browser observation. `historyLimited=false` on the task response; this is not a complete record of all worker internals or authenticated human actions. |
| [Prior recovery account](task-review-ux-plan.md#what-we-are-fixing) | Repeated observations consumed attempts; a prepared correction could not publish while blocked; an operator requeue recovered the task | A narrative account. Do not infer missing run boundaries from it. |
| [Prior UI/CI qualification](sprint-ui-qualification.md#production-reference-from-november-18-2026) | Historical delivery intervals and browser capture references | Its raw `timing/pr150` directory was not available at the checked Linux sprint-review path during this reconstruction; those phase durations are attributed, not independently recalculated here. |
| Git objects below | Commit ancestry and exact file/tree comparisons | Commit timestamps are not publication, CI, merge-event or deployment timestamps. |

The task can be revisited through the deployed dashboard's `/tasks/client-2?project=forge` route, with the selected project explicit. The snapshot provides durable evidence without depending on a private dashboard address or an active SSH tunnel.

## Review decisions and revisions

| UTC | Observed event | Exact source |
| --- | --- | --- |
| 01:33:13 | PR opened for the dashboard DevTools correction. | [PR #150](https://github.com/Xyrces/Forge/pull/150), `created_at` in the retained GitHub response |
| 01:35:15 | Round 1 approved the initial dashboard change. | [Original comment](https://github.com/Xyrces/Forge/pull/150#issuecomment-6966317332), [head `66ca85f89c1510cf8f31045ec381e1d81707d441`](https://github.com/Xyrces/Forge/commit/66ca85f89c1510cf8f31045ec381e1d81707d441) |
| 01:55:21 | Round 2 requested removal of an unrelated orchestration-test change. The dashboard change itself was still accepted. | [Original comment](https://github.com/Xyrces/Forge/pull/150#issuecomment-6966646881), [head `190d128b59c88c1ff684ffb5a2c941c65032888d`](https://github.com/Xyrces/Forge/commit/190d128b59c88c1ff684ffb5a2c941c65032888d) |
| 02:16:37 | Round 3 approved the restored scope; it explicitly left current-head CI and the separate Development extension check pending. | [Original comment](https://github.com/Xyrces/Forge/pull/150#issuecomment-6966990156), [head `263bbfeb44a12565e3738b01ae08bbc92283ae63`](https://github.com/Xyrces/Forge/commit/263bbfeb44a12565e3738b01ae08bbc92283ae63) |
| 02:20:08 | GitHub records the PR merged. | PR `merged_at`; [merge `30a6ec9f8b740dd4bd2775b26edac0db7ee48997`](https://github.com/Xyrces/Forge/commit/30a6ec9f8b740dd4bd2775b26edac0db7ee48997) |

All three reviewer comments were posted under **Xyrces** and contain Forge reviewer prose. Their text labels are not GitHub-native `APPROVED` or `CHANGES_REQUESTED` decisions. They do not prove independent GitHub App identities, an enforced human hold, native inline resolution, or today's protected merge gates. The fetched records have `review=null` and no inline path/line metadata.

Git comparisons performed for this report establish:

- `66ca85f` → `190d128` changes only `tests/Forge.Tests/Integration/OrchestratorAgentTests.cs`.
- `190d128` → `263bbfe` changes only that same file.
- The entire diff `66ca85f` → `263bbfe` is empty. The final tree therefore restores the initial reviewed content, not merely the affected file.
- The merge's second parent is `263bbfeb44a12565e3738b01ae08bbc92283ae63`; its first parent is `71e47dbc6dfc21bdcfa457e98dc188196c9d5495`.
- The merge commit time is 02:20:07 UTC, one second before GitHub's merge-event timestamp. These are distinct sources, not a discrepancy to conceal.

## Recovery sequence

The [retained Forge events](pr150-history-evidence.json) give the following observable sequence. Event IDs are stable identifiers within the retained store, not inferred worker-run numbers.

| UTC | Event ID | Observed state/action |
| --- | ---: | --- |
| 00:41:24.790 | 267 | Dependency on `task-5` added. |
| 00:42:39.155 | 274 | A worker plan was rejected after two revisions. The retained error concerns ownership of Production DevTools registration outside clientdev territory. This earlier planning problem is separate from the later scope review. |
| 01:55:25.307 | 367 | InProgress → Pending after round 2. |
| 01:55:27.480 | 370 | Task claimed by clientdev. |
| 01:56:07.453 | 375 | InProgress → Pending again. This transition alone does not establish a second independent finding or completed worker attempt. |
| 01:57:07.214 | 378 | Pending → Blocked; recorded reason is the changes-requested circuit breaker. |
| 02:02:17.950–02:05:56.122 | 379–381 | Three Blocked → Blocked records. They are not three new failures. |
| 02:07:02.388 | 382 | Blocked → Pending, with an operator-requeue detail saying failure/rework bookkeeping was cleared. |
| 02:07:04.961 | 386 | Claimed by clientdev after requeue. |
| 02:16:03.4094312 | delivery field | Retained trusted verification time for final head `263bbfe`. |
| 02:20:10.6854012 | delivery field | Forge observed merge `30a6ec9`. |
| 02:20:10.922 | 405 | InProgress → Completed. |

The earlier recovery narrative explains why publication stopped despite a prepared restoration: the task had become blocked after repeated observations of the same verdict. The event sequence corroborates the block and recovery; it does **not** expose an exact preparation/publication-rejection boundary. Do not reconstruct one from unrelated same-state events.

Event 382's detail says “operator requeue from Failed,” but its actual transition is **Blocked → Pending**. Retain that discrepancy. The event's recorded actor is `orchestrator`, not an identified human operator. There is one operator-requeue event in the returned history; neither that count nor the earlier narrative supplies the human's login or UI action timestamp independently.

## Bounded elapsed intervals

These are differences between named observations, not estimates of exclusive model time, worker CPU time, queue latency, or human wait. Intervals overlap and must not be summed as independent phases.

| Boundaries | Elapsed | Interpretation |
| --- | ---: | --- |
| GitHub PR creation → round 1 comment | 2m 02s | Publication-to-comment elapsed time; includes any scheduling and review work. |
| Round 1 → round 2 comments | 20m 06s | Includes intervening revision activity; not a pure review duration. |
| Round 2 → round 3 comments | 21m 16s | Includes correction, blocked period, recovery, verification and review. |
| Event 378 blocked → event 382 requeue | 9m 55.174s | Time between recorded task transitions; not a measured human response time. |
| Event 382 requeue → event 386 claim | 2.573s | Recorded requeue-to-claim interval, not first-token latency or full dispatch eligibility delay. |
| Round 3 comment → GitHub merge | 3m 31s | Post-comment wait including remaining CI/gates; cannot isolate polling overhead. |
| GitHub merge → Forge merge observation | 2.6854012s | Cross-system observed interval; precision reflects stored timestamps, not a clock synchronization guarantee. |
| GitHub PR creation → merge | 46m 55s | PR lifetime to merge, excluding earlier intake/planning and subsequent release. |

The prior qualification reports final-head CI **186s**, merged-head CI **194s**, image packaging **257s**, image completion to rollout Job start **163s**, and rollout Job duration **80s**. Main CI and packaging overlapped. These remain **secondary-source measurements** here because the raw receipt directory was not located at the documented path; they are not newly verified latency measurements. No precise model/tool, verification-only, or human-wait breakdown is supported by the collected records.

## Merge, deployment and acceptance are separate

The current retained completion record for Sprint 3 is `Satisfied`, with `Deployed` candidate `deploy-66e2d2abc074b9e62c185f12ed44c4be`, source `30a6ec9f8b740dd4bd2775b26edac0db7ee48997`, and plan hash `39ac30c02e260b6d0732e13dfa2fd0a4b4426114b924ec5eadb984503ff229ba`. The snapshot includes the recorded observations for `development-extension`, `production-browser-matrix`, and `production-fluxor-and-pilot`; their stored identity is “authenticated shared dashboard operator.” This is not an independently identified GitHub reviewer.

Those persisted observations substantiate that Forge recorded historical acceptance separately from merge. This report did not rerun the browser extension, recapture Production, inspect a current serving image for that historical release, or verify every external proof target. It does not transfer historical acceptance to PRs #158/#159 or a newer release.

The prior 05:01 UTC browser audit also found desktop card clipping on the historical release: card right edges at x=1309 on a 1280-pixel viewport, despite document width 1280. That sourced defect illustrates why a page-width-only assertion was insufficient; it is not evidence that the current redesign still has that defect.

## Remaining evidence limits

- No fresh raw GitHub CI/check inventory or K3s rollout receipt was acquired for this historical reconstruction; use the attributed earlier report for those durations.
- The snapshot is an API projection, not an immutable database backup. It preserves its collection time and selected original fields; no secrets or credentials are included.
- The record does not identify the human who initiated recovery, exclusive agent execution time, or the exact blocked-publication boundary.
- Review prose, task lifecycle, GitHub merge, recorded deployment and operator observation are separate kinds of evidence. None alone proves an unattended or protected-review run.
- No historical state was replayed or changed. This tracked report is the standalone historical deliverable; retirement of duplicate `task-7` remains an explicit operator decision.
