# PR #155 follow-up backlog and review dispositions

These follow-ups come from the November 18 review of [PR #155](https://github.com/Xyrces/Forge/pull/155).
The numbers match the original findings. File locations refer to the reviewed
revision `1f798bde8c83308375fbdb5eeced9a9d92d1fef2`.

The first review contributed ten backlog items. A [second review](#second-review) added
five more on the corrected revision. Findings #7 and #11 have separate dispositions
below. Findings #1–4 and #16 are tracked in the
[five inline review threads](https://github.com/Xyrces/Forge/pull/155#pullrequestreview-5957748467).

## #5 — Measure and reduce repeated private Git imports

**Actionable performance follow-up; no large-repository failure measured.**
`AgentTools/WorkerVerification.cs:34–36,52–53` creates a private metadata import
for each of three Git commands in a tree comparison. The isolation path applies
in controller App mode, or on Linux when a trusted repository root is supplied.
`CommitPushPrExecutor.cs:86,286,412` can request three comparisons: nine imports
is a conditional normal-publication count, not an unconditional count or an upper
bound for all Git work. The middle comparison requires configured verification
commands and no hygiene failure; earlier exits and command-hash mismatch reduce
work. Other GitWorktreeService operations import separately.

`IsolatedGitMetadata.cs:207–253` copies allowed object/pack data and metadata.
`PublishData` hashes mutable metadata (`:131`), enumerates existing objects and
copies new objects (`:151–159`); it does not rehash all existing objects.
Graph-builder Git reads also import in App mode
(`Codebase/DotnetCodebaseGraphBuilder.cs:307,335`).

Acceptance:
- Benchmark representative Forge and prospective larger repositories. Record Git
  object bytes/file counts, pack sizes, worktree count, import/verification wall
  time and temporary disk peak, separately from model and build time.
- Distinguish cold/warm cache and per-command/per-publication costs. The existing
  `OptionalReadOnlyRepositoryTiming` test is opt-in and has no latency threshold;
  passing the ordinary suite does not establish large-repository performance.
- If justified, reuse one isolated context within a tree computation while
  retaining separate freshness checks before publication. Qualify concurrent
  ref/index changes and new object publication with existing CAS tests.
- Do not introduce shared writable hardlinks, arbitrary alternates, repository
  configuration or executable hooks into the trusted controller as a shortcut.

## #6 — Diagnose unsupported repository layouts before dispatch

**Actionable diagnostics follow-up; fail-closed behavior is intentional.**
`IsolatedGitMetadata.cs:276–282` uses `openat2` with no symlink resolution in any
path component. `:30,267–270` may surface a missing `.git` as a filesystem
exception. This limitation is already documented in
`docs/github-app-identities.md:40`; ordinary bind mounts are not inherently
symlinks, so Kubernetes/PVC use alone does not establish a failure.

Acceptance:
- Add a read-only check of registered repository/worktree layout and required
  Linux filesystem capabilities before paid work; identify the affected project
  and unsupported layout without logging credentials.
- Test normal clone/worktree paths, a symlinked ancestor, missing metadata, and
  the documented per-file size limit. Preserve rejection of untrusted links.
- Surface an actionable configuration error rather than claiming GitHub
  authentication failed or relaxing the isolation boundary.

## #8 — Validate supported SQL principal names clearly

**Confirmed configuration limitation.** `Core/Db/MemoryAuthoritySql.cs:49–51`
uses the same restrictive identifier expression for schema and worker name;
`InstallAsync:10` also validates the controller this way. Valid SQL principals
containing `.`, `@` or `$` are excluded. Existing qualified Forge identities do
not demonstrate this failure.

Acceptance:
- Narrow first step: validate the actual configured worker/controller principal
  names before installation and name the unsupported option in the diagnostic.
- If broader principal support is needed, separate schema validation from
  principal quoting. Correctly escape both bracketed SQL identifiers and the
  Unicode string literal used by `USER_NAME()` comparison; broadening only the
  regex is insufficient.
- Test representative supported names and rejected invalid input, distinct-role
  enforcement, and actual grants/trigger behavior using a disposable SQL schema.

## #9 — Detect an unfenceable custom workflow before first upgrade

**Confirmed compatibility prerequisite, not permission to bypass rollback safety.**
`Core/Control/DeploymentMergeFence.cs:12–19` refuses workflows without an enabled
merge-gated step. `Dashboard/WorkflowEndpoints.cs:156–158` prevents removing that
step only after a persisted fence exists. A previously customized workflow can
therefore prevent its first fenced upgrade; refusal preserves the older service.

Acceptance:
- Read-only upgrade preflight identifies the exact project/workflow missing the
  required gate before candidate activation/drain and explains the supported
  workflow repair.
- Test pre-existing incompatible workflow, compatible custom workflow, no live
  override, and attempts to remove a gate after fencing.
- Any earlier workflow-publish validation should explain this deployment
  prerequisite without silently rewriting operator intent. Do not waive a
  safety fence merely by recording an acknowledgement.

## #10 — Bound task-list restoration cost without losing navigation

**Confirmed serial replay; latency is inferred, not measured.**
`Forge.UI/Components/Pages/Tasks.razor:68–69` restores one initial page followed
by up to 99 serial pages. It stops on missing cursor/error or an invalidated
restoration generation. Existing tests cover interruption by project/filter/
search changes; browser qualification covers two pages, not a large backlog.
The API's read-authentication posture does not itself demonstrate a new
permission defect in this navigation code.

Acceptance:
- Measure request count, bytes and time until usable content for a realistic
  large history, including slow responses and a saved deep scroll position.
- Consider a smaller initial replay window with an explicit way to resume,
  cursor/anchor-based restoration, or a bounded session cache. Preserve the
  selected project, filters, task anchor, Back behavior and ordinary paging.
- Add deterministic request-count bounds and cancellation/navigation tests;
  verify mobile and desktop without replaying 100 pages to show initial content.

## #12 — Parameterize task-scoped active-run reads

**Confirmed maintainability improvement; no injection established.**
`Core/AgentRunStore.cs:223` escapes apostrophes before interpolating task ID.
Replace this hand-escaped value with the store's parameter seam rather than
claiming the existing escaping is an observed injection.

Acceptance: parameterized task ID on both SQLite and SQL Server; preserve null
meaning all active runs, exact task matching, ordering and project-store scope.
Test apostrophes and non-ASCII IDs using inert data.

## #13 — Bound project-policy reservation waits

**Confirmed contention risk; no measured indefinite hang.**
`Dashboard/TaskReviewEndpoints.cs:105–112` sequentially reserves gates for all
project tasks except Completed/Closed, including Failed/Blocked rows. Earlier
reservations remain held while later ones are acquired. `WaitAsync(ct)` is
request-cancellable and the `finally` releases acquired gates; there is no
explicit server-side save deadline. Watcher polls skip a busy gate rather than
waiting (`Reviewer/PRWatcher.EmptyPr.cs:46`). Existing endpoint tests verify
reservation ordering and policy scoping, not a latency bound under contention.

Acceptance:
- Add a bounded acquisition deadline with an actionable retry response; do not
  silently save a partial policy or misreport success.
- Test a held later gate, request cancellation, timeout release of earlier
  reservations, successful retry, and unchanged saved/audited state on failure.
- Preserve the merge-versus-policy serialization guarantee and wake the watcher
  only after releasing reservations. Measure contention before optimizing away
  apparently inactive tasks or replacing per-task coordination.

## #14 — Coalesce navigation persistence on scroll/render

**Confirmed unnecessary hot-path work; jank is not measured.**
`Forge.UI/wwwroot/sprint-board.js:13–19,39` synchronously parses, serializes and
writes storage on captured scroll events while the board/list exists and
restoration is inactive. `Sprints.razor:658–662` saves a populated board after
renders when neither loading nor refreshing. It does not write on every render
unconditionally. Existing browser tests establish navigation correctness, not
storage-write frequency or frame cost.

Acceptance:
- Coalesce scroll writes and skip unchanged location payloads. Flush the latest
  position/focus before navigation so throttling does not lose Back behavior.
- Assert bounded write counts during repeated scroll/render events; keep
  project/list keys separate, preserve keyboard focus and the latest position,
  and tolerate unavailable session storage.
- Repeat existing six navigation journeys and mobile lane tests; measure frame
  and storage work instead of asserting a speedup from code shape alone.

## #15 — Make unaudited review-mode mutation explicitly internal

**Confirmed API-footgun, not a current authenticated-endpoint bypass.**
`Core/PullRequestReviewPolicy.cs:36–37` exposes unaudited `SetModeAsync(mode, ct)`;
current operator endpoints use audited setters. Repository callers of this mode
overload are tests. The unaudited hold overload is different: the watcher uses
it to synchronize label state, so do not remove all similar methods blindly.

Acceptance: restrict or clearly name the test/internal mode-writing seam;
validate modes consistently; assert operator writes retain actor/audit records
and nomination data. Audit actual callers before changing hold synchronization.

## #17 — Remove unused helpers and correct validation wording

**Confirmed low-priority cleanup.** No callers remain for
`GitWorktreeService.NormalizeRepositoryUrl` (`:688`) or
`IsolatedGitMetadata.WriteText` (`:262`). `GitWorktreeService.cs:682–683` rejects
quotes, backslashes and NUL as well as whitespace, but names only whitespace.

Acceptance: remove dead helpers; accurately describe allowed Git components
without exposing credentials; retain validation behavior and focused tests.
No runtime performance improvement is claimed for deleting unused methods.

## #7 disposition — Keep terminal explicit verdict parsing

**Do not adopt the proposed permissive substring parser.**
`Reviewer/FormalReviewEvidence.cs:35–42` accepts the last nonblank line, trims
it and compares case-insensitively to the two explicit verdicts. Trailing blank
lines already work. The prompt explicitly requires this terminal marker. Text
after a marker is rejected intentionally; accepting a marker buried in quoted
source or earlier discussion risks treating ambiguous output as approval.

Narrow improvement if needed: retain safe parse-failure diagnostics and test
blank endings, casing, missing/ambiguous markers and trailing prose. Address
bounded escalation/recovery of reviewer errors separately under #1. Do not log
an unrestricted raw tail merely to diagnose protocol failures.

## #11 disposition — SQLite is not the qualified worker authority boundary

**Not a demonstrated bypass of this SQL Server production deployment.**
Generic memory endpoints reject protected keys. Production worker restrictions
are additionally enforced by SQL Server identities/triggers, with separate
controller authority and rollback-compatible stored controls. A blanket deny in
`MemoryStore.RememberAsync/ForgetAsync` would also block trusted policy and
synchronization callers; that store does not carry a caller's authority.

The limitation for direct SQLite writes is real for a hypothetical untrusted
worker sharing writable SQLite storage. If that becomes a supported deployment,
define an explicit trusted-control API/capability and worker isolation contract,
then test operator success and worker refusal across every write path, including
memory extraction and direct storage access. An in-process guard alone cannot
protect SQLite from a process that can write the database file. Keep the current
support boundary and SQL qualification documentation explicit.

## Second review

Findings #18–22 refer to [`3318015`](https://github.com/Xyrces/Forge/commit/33180158785cee89c6fb643409fa6c3a984c996c).
All five are deferred. The scope and acceptance checks below are based on source review.

### #18 — Show when a second project is still provisioning

The registration request stops waiting after 75 seconds. The supervisor Job allows 900 seconds
and its Python loop allows 840. If the HTTP wait expires first, the endpoint returns the same
503 as a terminal Job failure. The Job can keep running. Retrying the same registration can
adopt it, but the operator has no way to tell when that is the appropriate next step.
[Controller wait](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Orchestrator/Control/KubernetesProjectProvisioner.cs#L24)
· [Supervisor wait](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/deploy/k3s/project_provision.py#L27)
· [HTTP outcomes](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Dashboard/ProjectsEndpoints.cs#L128)

**Acceptance.** Give ongoing provisioning a distinct response and UI state that preserves the form.
Account for the UI client's own timeout. Exercise a healthy Job that takes more than 75 seconds,
retrying that Job, and a terminal failure. Keep the registry row hidden until attestation succeeds.
Preserve the existing `202` outcome for a project whose registration succeeded but whose clone failed.

### #19 — Align project provisioning with Kubernetes client availability

A controller creates the provisioner with workers disabled, but only the workers-enabled path
registers its Kubernetes HTTP client. In a configuration that reaches the dashboard, the missing
dependency first surfaces as a 503 during registration. The shipped K3s manifest enables workers.
`RuntimeFactory` is a separate, optional test hook. Production already falls back to the
configured SQL connection with automatic schema initialization disabled.
[Service registration](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Orchestrator/Composition/ForgeComposition.cs#L341)
· [Provisioner wiring](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Orchestrator/Composition/ForgeComposition.cs#L785)
· [SQL fallback](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Orchestrator/Control/KubernetesProjectProvisioner.cs#L70)

**Acceptance.** Test controller mode with workers enabled and disabled. A supported configuration
must have the client and immutable image needed for provisioning. An unsupported configuration
must identify the missing capability before an operator submits a registration. Both paths must
enforce the storage checks before publishing a project.

### #20 — Bound contention when retrying a review

Retry acquires the task's poll gate and reviewer gate in sequence. Request cancellation releases
acquired gates, but there is no server deadline for acquiring both. The review gate was available
when the watcher blocked the task. Another operation can hold it by the time the operator retries.
This is the task-level counterpart to #13's project-policy save.
[Retry endpoint](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Dashboard/TaskEndpoints.cs#L328)

**Acceptance.** Use one acquisition deadline across both gates. Return a retryable busy response
when it expires. Exercise each gate under contention, client cancellation, and a successful later
retry. A timed-out request must release any acquired gate and preserve the task, counters and
publication evidence. A successful retry must wake the watcher after releasing both gates.

### #21 — Name the review retry action in the triage ledger

The ledger already stores `operator-retry-review`. The shared `FailureTriageActions` vocabulary
lacks a constant for it. The transition from Blocked to InProgress already signals clearance.
[Retry metadata](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Core/ReviewIntegrationState.cs#L25)
· [Ledger consumer](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Orchestrator/Consumers/FailureTriageConsumer.cs#L96)

**Acceptance.** Use a declared constant with the existing stored spelling and verify that one
retry records the expected operator action. Existing entries must remain readable.

### #22 — Match the retry button to the recovery the server will perform

Task Detail chooses its label and POST route from `blockedKind`. The endpoint also requires
the task to be Blocked. Guided triage can move a task to Pending with the old kind intact.
If engineering subsequently fails, the Failed task advertises **Retry review** but takes
the generic engineering requeue path. That can reset engineering strikes while retaining
exhausted review attempts. If the watcher blocks the task again on the same revision, the
review-specific action becomes available again.
[Task action](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Forge.UI/Components/Pages/TaskDetail.razor#L683)
· [Server predicate](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Core/ReviewIntegrationState.cs#L8)
· [Guided requeue](https://github.com/Xyrces/Forge/blob/33180158785cee89c6fb643409fa6c3a984c996c/Agents/TriageTools.cs#L79)

**Acceptance.** Use the same recovery decision for the displayed action and server mutation.
Test a genuinely review-blocked task, a Failed task with an old marker, an ordinary engineering
failure, and a task whose status changes after the page loads. Cover exhausted model attempts
and publication attempts separately. A review-only retry must preserve the saved draft,
uncertain publication state, human hold and engineering strikes.
