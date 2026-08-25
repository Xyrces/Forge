# Fix-and-prove: QA verdict landing + requeue QA-budget reset + epic-11 hygiene + escalation target

## Context (all verified live 2026-08-24)

Deploy of 33a26a9 succeeded (V1/V2/V3/V6 verified). task-740 ("Audit ShipFactory.CreateShip
axis mapping", porthorizon, PR #1039) is the proving ground and is stuck in a three-cause loop:

1. **QA pass verdicts silently fail to land.** The 19:58 UTC QA run returned a full
   `QA_VERDICT: pass` at head 6b26e15 with raster PNGs in the QA worktree (all dirty paths under
   `test-results/`), yet no evidence commit reached the branch and no `qaSha/qaVerdict` metadata
   updated. Every `VerdictError` return in `Reviewer/QaDispatcher.cs::RunQaAsync` (sync mismatch,
   timeout, no marker, non-evidence paths, pass-without-raster) returns WITHOUT logging, and the
   `WatchSweepService` continuation only logs faults — so the failure reason is invisible.
   Root cause UNKNOWN until observability lands; candidates for the implementer:
   (a) the background QA task inherits the sweep's CancellationToken — if it cancels between run
   end and the commit/push, the OCE escapes the `catch (ex) when (ex is not
   OperationCanceledException)`, the continuation sees a canceled task and throws accessing
   `t.Result` — unobserved, no logs; (b) `result.Text` not carrying the final message on some
   session shapes; (c) sync-ref staleness. NOTE: one full pass DID land (14:29 UTC, commit
   4e38ae3 with PNG + metadata), so the path works when whatever-this-is doesn't bite.
2. **QA attempt budget never resets on operator requeue.** `Dashboard/TaskEndpoints.cs:283-300`
   clears retry/strike/rework bookkeeping but not `qaAttempts`/`qaAttemptSha`/`qaStartedAt`
   (or `blockedKind`). After a `qa-unavailable` block, a requeue at the same head re-blocks
   instantly; a new head just burns 2 more silent attempts. Observed: blocked 16:58 → triage-park
   → requeue 18:06 → blocked 20:58 → triage-park. task-740's triage daily cap (2/day) is spent.
3. **epic-11 stuck in ReadyForDesign.** DesignerScheduler hygiene fails every 15m:
   `missing_acceptance_criteria` (no `## Acceptance criteria` section) + `touches_undefined_module`
   (`## Touches` references module `TBD`). Content defect; blocks the capture hook that makes
   raster QA systematic.

Already proven (no work needed): **V5** — triage agent fired on both crossings, parked with
evidence-cited operator-grade reasoning (ledger rows 8, 9). **V4 partial** — one full
QA-before-review cycle landed end-to-end (raster evidence push → verdict metadata → reviewer
followed). **V7 pending** — needs the escalation target set (this plan) + a natural
capability-bound failure (do NOT force).

## Operator decisions (2026-08-24)

- Bundle: all three fixes — one forge PR (observability + requeue budget reset) + the porthorizon
  spec edit; then requeue task-740 and ride it to merge as the V4 proof.
- Escalation target: coredev → the kimi subscription's **2.7** model (provider `kimi`,
  api.kimi.com/coding/v1; sibling ids are `k3`/`k3-256k`/`kimi-for-coding`). Project-scoped to
  porthorizon. Exact model id probed at execution time (task 3). k3 remains the fallback if 2.7
  misbehaves.

## Tasks (ordered)

1. **Forge PR — observability + budget reset** (feature branch; branch protection applies):
   a. `Reviewer/QaDispatcher.cs`: every `VerdictError` return logs a warning (task id, head,
      reason) and stamps `qaLastError`/`qaLastErrorAt` task metadata (TaskDetail-visible). Include
      the budget-exhausted park path. Also investigate candidate (a) — if the background QA run
      must outlive the sweep, decouple from the sweep CT (own linked CTS with the 30m timeout);
      check `TryLaunchBackgroundReviewAsync` for the same exposure while there.
   b. `Orchestrator/WatchSweepService.cs`: QA continuation logs non-pass/non-current outcomes
      (verdict + note), not just faults.
   c. `Dashboard/TaskEndpoints.cs` operator requeue: also clear `qaAttempts`, `qaAttemptSha`,
      `qaStartedAt`, and `blockedKind` (when `qa-unavailable`) — operator intervention resets the
      per-head QA budget exactly like it resets strikes.
   d. Tests: requeue clears the QA budget keys (endpoint test); a `qa-unavailable`-blocked task
      re-attempts QA after requeue at the same head; logging is side-effect-only (no behavior
      change asserts beyond metadata).
   e. Validate: full suite green, `TreatWarningsAsErrors` clean, `--check` passes.
   f. Deploy as release 50 per the runbook shape (`.kilo/plans/1786201235000-deploy-f515cc5-live-verify.md`
      §Deploy steps) — operator-authorized; no gate holds needed (no model-config window), but
      confirm no active runs first.

2. **porthorizon spec edit — epic-11 hygiene** (no code): add `## Acceptance criteria` with
   checkboxed bullets derived from the spec's scope items (capture hook produces a non-blank
   client-viewport PNG headless; tier-0/tier-1 evidence redone as client-viewport PNGs; JSON/SVG
   fixtures demoted from evidence) and replace the `TBD` module in `## Touches` with real module
   ids from the codebase graph (check the graph first; the client boot/capture area). Use the
   dashboard spec-edit surface or `/api/specs/{id}/actions` — whichever supports body edits.
   Pass = DesignerScheduler's next tick reports hygiene green and the design lane advances.

3. **Set the escalation target** (after the release-50 deploy, any time): probe the kimi endpoint
   for the exact 2.7 model id (cheap `GET /models` or a 1-token completion with the candidate id,
   using the process's configured key — never print the key), then
   `PUT /api/agents/roles/coredev/escalation-model` with
   `{"provider":"kimi","model":"<2.7 id>","projectId":"porthorizon"}`. Verify on /agents (coredev
   card shows the escalation model, source override (project)).

4. **Requeue task-740 and ride it to merge (the V4 proof):**
   `POST /api/tasks/task-740/requeue?projectId=porthorizon`. The cleared QA budget lets the next
   attempt run at the current head; the NEW log line/`qaLastError` names the silent failure.
   Fix forward in a follow-up PR if it's a real defect; repeat until the pass lands
   (`qaVerdict=pass` at head) → reviewer runs → merge gate → merge. Capture evidence at each step
   (journal lines, ledger rows, metadata, dashboard screenshot).

5. **V7 observation (no forcing):** with the escalation model set, the next capability-bound
   failure crossing lets the triage agent `escalate_model` → marker → escalated run on kimi 2.7
   (run label + `modelEscalated` metadata + `escalated:coredev` slot draw only). If nothing fails
   naturally, V7 stays pending — mechanism is test-covered.

## Risks / notes

- The silent-failure root cause is deliberately not guessed in this plan; task 4's read-the-log
  step decides the follow-up. If candidate (a) (sweep-CT) is confirmed in code review during
  task 1, the CT decoupling rides the same PR.
- task-740's triage cap resets daily; if the deterministic same-signature park rule engages on a
  new crossing, that's correct behavior — the operator requeue path (task 4) is the manual override.
- Requeue clearing `blockedKind` is hygiene (inert once Pending) but keeps the metadata honest.
- epic-11 unblocked → design → groom → sprint; its task-1 PR self-demonstrates the in-branch
  capture hook under the QA stage.

## Out of scope

- Changing the QA verdict contract, attempt budget size (2), or raster-evidence bar.
- Auto-resume from `qa-unavailable` (operator-decision by design).
- Failure-triage phase 4; per-project-per-model slot limits.
