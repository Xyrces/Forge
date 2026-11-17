# Unattended workflow live qualification

Latest result: the approved pilot reached **Satisfied**, with automatic deployment and explicit operator acceptance on 2026-11-17. See [the final qualification](#final-qualification-2026-11-17). Earlier blocked checkpoints below remain chronological evidence, not current status.

## Version 41 release

On 2026-11-17, PR #131 merged as `3ca60c60610e968111247f7387d6e20226b97654`.
The exact packaged source `8628aa65f0a55d63955e3895d51f99675640c822` passed
CI `44138872845`: 1,986 .NET tests, 44 release/installer tests, and the
deterministic engineering-to-review harness. Image workflow `44138884422`
independently built and tested immutable image
`ghcr.io/xyrces/forge@sha256:688720980720a6ace8eb8f3395cfd95a4df1d34d30edf3af1de007f6476ae9e3`.

The first combined harness attempt exposed an incomplete simulated GitHub
merge response. The adapter now returns the accepted head, merge SHA and
merge time; the harness asserts that exact evidence. Production review
requirements were not weakened to make the harness pass.

Normal candidate/approval operation `deploy-138538c4cbb84ec7923c6a5624072754`
ran the coordinated 40→41 upgrade. Its initializer completed, the release
journal committed, and the independent release Job completed in 86 seconds.
The blue controller `df67563f-c04e-4423-ba1a-955b6d100674` became active and
ready at epoch25. Its `/app/source-revision` matched the qualified receipt.
The read-only application check passed at schema41 without model generation.

Both `core` and `proj_forge` advanced to41. Dataset
`f610a39c-50e1-4aab-8d5a-e18e10aeede9`, the $25 authorization, all four
existing stage holds, and full canonical snapshots of the three budget tables
were preserved. The actual worker credential passed 28 permission checks:
SELECT remained available and INSERT/UPDATE/DELETE were denied on the five
completion authority tables, deployment and sprint proposal audit. This is
permission evidence for that identity, not hostile-worker isolation evidence.

The private deployment configuration enabled routine release for Forge on
the new controller start. The mounted configuration and private installer
copy were updated together and checked. Ordinary routine release still
requires exact merged-source CI and immutable image evidence; schema changes
remain coordinated maintenance operations.

The qualified40/41 bridge was the pinned rollback image. This release did
not inject a new rollback failure. The earlier rollback exercises and the
[isolated SQL compatibility rehearsal](completion-sql-qualification.md)
are distinct evidence, not claims of a fault during this successful cutover.

## Live Intake journey

At17:44 UTC the actual dashboard approved proposal
`proposal-998a7718c7bb43a1928a4f9de029344a`, revision1, hash
`5f909698916d48285aebd6439b7c6038af865394f28315583adcfaaae961e21d`.
The normal transaction created `epic-1` and
`spec-bc3ea950baff4860aad06036a6ed4c6b`. Product refinement completed once
at17:45:09. The specification retained artifact-only running revision
authority, compatible API fields, global UI behavior and separate worker
versus operator verification. All four stage holds were released through
the Flow UI; routine deployment was already open.

This live journey then exposed a real repository freshness defect. The
managed clone and its remote-tracking branch remained at `1b733cb`, while
GitHub reported `3ca60c6`. Grooming read the stale clone and first-run
worktree creation used the stale local branch. Design, grooming and sprint
admission were held before grooming or engineering began. The approved
initiative and completed product refinement were retained. A manual sync
would repair this checkout once, but would not establish unattended
correctness; automatic planning and worktree refresh require a system fix.

The live specification also exposed raw Markdown presentation and an
unhelpful "no available actions" explanation for an automatic design wait.
Those observations are UI findings, not successful journey qualification.

Private receipts and browser screenshots are retained under
`.portHorizon/validation/` in the integration worktree. At this checkpoint,
the new pilot has **not** reached grooming, implementation or delivery.

## Freshness repair and resumption

PR #132 merged as `3402111d8cef76c70a6454ab0a3552c9c133ceca`. Packaged
source `c03555850d77dfc061efea6329dadf0b06476ebe` passed Linux CI
`44143791179`: all 2,004 .NET tests, 44 deployment checks and the offline
engineering/review harness. Image workflow `44143780380` passed and produced
`ghcr.io/xyrces/forge@sha256:e6c754655d7dafefa7f2f54bfab601b6176eed36734d97e6ae231eac132b89c9`.
The earlier run's missing-remote fixture failures were corrected with
disposable local remotes, preserving the production fresh-fetch requirement.

Normal release `deploy-3d96410462074df797d83fc475197196` committed in 80
seconds. Green controller `e528ac64-9672-4b89-aa0b-fc1592ee62eb` became
active and ready at epoch 26, schema 41. Its image source matched the receipt.
On activation the managed checkout advanced from
`1b733cb32fcfa97b70d4ab00736f3f985bf23986` to the exact PR132 merge above.
No manual project sync, git pull, checkout or queue repair caused that update.
The read-only application check passed; spending-table snapshots and the
planning holds were preserved across the release.

Planning now refreshes the configured owning checkout before claiming a
paid grooming attempt, and new engineering worktrees fetch and pin a remote
commit. Edited or unknown origin-less work is preserved. A clean scaffold
created by Forge can heal only when its recorded bootstrap commit matches.
Existing-worktree rework remains supported; a missing worktree with an
existing orphan branch fails closed rather than resetting unknown work.
These boundaries are explicit and are not claims of automatic recovery
from every filesystem loss.

The deployed specification page rendered the live proposal's refined
Markdown and explained the automatic design wait, including stage holds.
At approximately 18:13 UTC, the Flow UI reopened design, grooming and sprint
admission; merge and deployment remained open. The same approved initiative
and completed product refinement were retained. At resumption the budget
readback was $1.01 operator-reported prior cost, $0.075166 newer usage
estimates, no outstanding/uncertain requests and $23.914834 available.
This paragraph records resumption, not completed pilot delivery.

## Specification validation finding

The approved pilot reached Product refinement, which persisted specification
version 2. Before any Designer model call, deterministic validation rejected
its Dependencies section: Product had written the parent epic ID `epic-1`
where the specification format requires an existing same-project spec ID.
Designer runs 1 and 2 recorded this failure; no engineering work was created.
This is a producer validation defect, not evidence that epic IDs should be
accepted as spec dependencies. The path-based Touches section was also
incorrectly described as empty; those entries provide context but are not
validated module identifiers.

The correction rejects missing, cross-project, self-referential and cyclic
spec references before Product saves a new version, returning actionable
feedback within its bounded model run. A separate explicit repair action
is being qualified for the already-saved pilot: it must retain the original
approval and version history, bind to the exact current version and latest
failed validation run, preserve the diagnostics durably, and refuse when
downstream work has begun. Neither a hand-edited live spec nor a replacement
initiative qualifies this recovery path.

## Guarded repair deployed and requested

PR #133 merged as `8c29f524bb389178716c562a78bda5e6af9875dd`. Source
`d1b8a6ba548c1230bc96b7edf53c673b0d94f880` passed Linux CI and image
qualification, plus the separate Azure SQL recovery checks described in
`spec-hygiene-repair.md`. Its immutable image is
`ghcr.io/xyrces/forge@sha256:20eab8d5d1355aad2c0e74f8f6fd5e879ff1d433f8280623536e24a432cc5d2a`.

Normal operation `deploy-d3dc023bb77c4db481f86b158069aa02` committed.
Blue controller `281623ef-21e3-4833-8190-232324a3cc61` became active and
ready at epoch 27, retaining schema 41 and the dataset. The running artifact
matched the image receipt; the managed checkout automatically advanced to
the PR #133 merge. Read-only preflight passed, spending-table hashes were
unchanged, and the approved pilot remained ReadyForDesign v2 with completed
refinement before the explicit recovery action.

At 18:41:50 UTC the operator-side browser clicked **Refine again** on the
original live specification. The UI then showed Draft v2 and refinement
Pending, explained that no second initiative approval was needed, and
removed the retry control. The original proposal, epic, body and version
history were retained. Before this paid repair started, usage remained
$1.01 operator-reported prior cost plus $0.075166 newer usage estimates,
with $23.914834 available and no uncertain requests. This checkpoint proves
the deployed recovery action; later pipeline stages still require observation.

## Design and grooming reached; observation-only work held

The same approved initiative refined successfully to specification version 3.
Designer run 5 then succeeded with three design artifacts, and grooming run 1
finished at approximately 19:00 UTC with two stories and four tasks. These
were real model runs through the original intake journey, not seeded rows.

Grooming exposed a second defect. `task-2` and `client-1` formed one
implementation outcome with five pre-deployment criteria. `qa-1` and `qa-2`
formed a separate outcome containing only two operator-observed criteria;
their descriptions explicitly required checks after deployment. Such an
observation-only engineering outcome cannot honestly satisfy the requirement
that its executable members be merged and verified before it can deploy.

Sprint assembly was held through the Flow UI before any sprint or worker
started. Read-only inspection confirmed all four tasks remained Pending,
unassigned, without execution checkpoints, publication, or recorded sprint
membership. The only dependencies were the two implementation tasks blocking
each of the two observation rows. Approval identity and scope remained intact.

The correction is being qualified as an explicit, deterministic planning
repair: transfer the exact operator criteria to the implementation outcome,
retire the observation-only rows as Closed with a Cancelled disposition,
retain their history, and leave actual operator evidence pending. It must
reject stale previews, ambiguous destinations, prior execution or admission,
and partial writes. No paid grooming retry or fabricated completion evidence
is needed. This checkpoint is still a blocked pilot, not end-to-end success.

## Acceptance repair deployed and the real sprint resumed

PR #135 merged as `08402d915b9d276a3d8bc4be706dbb07f6f095ee`; the
additional serialized-binding bound in PR #136 merged as
`1bf875097b12b522a746f97aeda84060ab108152`. The released source is
`b28eeb123d85146ce9d31d5dbeef7b0ea8ca0f13`, qualified by Linux CI
`44160694581` (2,067 .NET tests, 44 deployment tests and the offline workflow)
and image run `44160701942`. Its immutable image is
`ghcr.io/xyrces/forge@sha256:cb2ae1436d7a4920df83b967027e0d9ef3adda99f6bfa6f50f4db41d7d32607b`.

Normal release `deploy-ef05cac8e2484c078ed01d05e837be60` committed in
51 seconds. Green controller `5fd25361-27ab-47b4-8186-5631bf1bb952`
became active at epoch 28 on the same schema-41 dataset. The running artifact
matched the receipt; read-only deployed preflight passed and all three
spending-table snapshot hashes were unchanged. The managed checkout advanced
to the PR #136 merge. Sprint admission remained held throughout the release.

The actual live specification UI then previewed and applied the correction.
Independent readback confirmed all seven criteria and their original scope
objects were unchanged, both implementation tasks remained Pending, and both
QA rows were Closed with Cancelled disposition and their original mappings.
The total set of nine issue IDs was unchanged; no PR/branch or delivery evidence
was fabricated. Each of the four affected rows gained one audit event and the
same version-3/run-1 repair reference. The proposal's revision-1 approval hash,
original approval timestamp, completed ingestion and Groomed v3 specification
were preserved.

The Flow UI released sprint admission at approximately 19:48 UTC. Forge
automatically assembled `sprint-7bf2555ec5` with exactly `task-2` and
`client-1`. Both reached AgentRunning concurrently in separate Kubernetes
Jobs, one CoreDev and one ClientDev. The live Sprints board showed Running 2,
Queued 0, and explicitly stated that no completion evidence existed yet.
This proves real assembly and parallel dispatch after the original intake,
design and grooming; implementation, review, deployment and final operator
acceptance were still in progress at this checkpoint.

## Worker phase timeout exposed by the live pilot

The first ClientDev attempt exhausted its bounded plan revisions: the task's
required documentation destination was outside its role territory, and the
critic then rejected an alternative location. The next attempt obtained an
approved plan and filed `task-3` for authorized documentation collation.
Its early sprint membership is visibility only. Grooming completed at about
20:01 UTC, and an audited prerequisite amendment is still required before it
can dispatch; pending visibility is not execution permission.

CoreDev passed its in-session build and full test suite, then passed the
independent worker build. At approximately 20:03 UTC its outer 15-minute
deadline expired during independent verification. ClientDev reached the same
outer deadline at about 20:05 UTC. Neither had published a PR. Read-only
inspection confirmed the backend's modified/untracked files remained in its
worktree and the UI's local commit `c4a90ff3b1de08dfc359266673e17ac8b8340f17`
was preserved. No manual publication or success state was substituted.

The source correction separates the configured model-work deadline from
independent verification and removes the duplicate automatic full-suite run
only in distributed workers. Independent exact-tree verification remains
mandatory, with its existing per-command timeout and Kubernetes attempt
ceiling. Legacy in-session verification behavior is retained. The timeout
recovery path also needs fresh failure timestamps and normal lifecycle
handling instead of leaving a Pending row with stale AgentRunning metadata.

At 20:06 UTC the budget recorded $1.722393 in newer usage estimates plus the
operator-reported $1.01 historical charge, no outstanding holds or uncertain
requests, and $22.267607 remaining under the $25 ceiling. Usage-derived amounts
are estimates, not an assertion of a reconciled billing charge.

The dashboard review also found no UI control for recording the operator
evidence required by the completion API. A new source-bound observation form
was exercised against an explicitly synthetic offline completion: the draft
survived refresh, recording saved the exact note with its hash, the view became
evidence-satisfied, and another refresh retained that result. This validates
the control, not the live pilot's visual or running-image acceptance. Long
sprint goals were also checked at desktop and 390-pixel widths with keyboard
expansion, retaining the full criteria text.

## Worker correction deployed; prerequisite admission and scheduling repaired

PR #137 merged as `d42615e09454c766c2aa7eb5727bf0eb54dc4aa7`.
Source `6b5c4be89d6b28240e373367e0e16c7c163b4d8f` passed CI
`44166642614` (2,091 .NET tests, 44 release/installer tests and the offline
end-to-end harness) and image workflow `44166660364`. Normal release
`deploy-4cff7b6092074c448b8f44e0f8fb14ad` committed image
`ghcr.io/xyrces/forge@sha256:98189b35495274e04d920235cd93dc0b5c32977978a298553846bd16b4ab88eb`.
Blue owner `e8e36555-906e-48b1-b93a-a02f4eb59d12` became active at epoch 29.
The running artifact matched; deployed read-only preflight passed. Schema 41,
the dataset, pilot rows and all three budget-table snapshot hashes were retained.

The stale backend timeout state was normalized through the existing project-scoped
park and guided requeue APIs after both old worker pods were confirmed absent.
This was an explicit operator recovery, not a worker publication or a direct queue
write. Its binding and worktree were retained; lifecycle became Pending and the
guidance required inspecting existing work before making further changes.

Two further defects prevented normal progress. The ad-hoc groomer had given
`task-3` a new `qualification-handoff` criterion, although the committed sprint's
existing `offline-build` criterion already required that handoff. The amendment
validator correctly refused the expansion. Separately, dispatch reserved the
only CoreDev slot for this inadmissible, priority-boosted blocker before checking
admission, repeatedly starving the eligible backend sibling.

The corrections ground active prerequisite grooming in the exact committed
criteria and enforce those constraints before persisting a mapping or grooming
approval. A snapshot-bound, explicitly requested re-groom operation can reset
eligibility only for untouched, parentless Pending tasks outside every committed
plan. It retains prior metadata in the audit and preserves bindings, dependencies
and initiative approval. The UI requires a reason, explains possible model cost,
and discards stale or uncertain action snapshots. Dispatch now checks the same
admission rule before reserving a slot and retains its pre-claim recheck.

Actual Azure SQL qualification of source `dd61eadbb7f5b0b0687391f9e5fd9039ab3f5a53`
passed preserved-state/replay checks, exact-criterion enforcement, audit-failure
rollback, concurrent single-winner reset, and committed-member/foreign-approval
refusal. The disposable workload schema was removed and absence verified. The
separate rejected-visibility-link transaction was likewise qualified on Azure SQL
at `0ef51d834eacf436ebb32e57b28545ac78353b51`. Neither rehearsal initialized the
shared Core schema or changed production rows or grants.

An offline browser requested technical review for a synthetic task, displayed the
queued confirmation, and removed the action after refresh. This demonstrates the
recovery control only: no model scheduler runs in that preview and the live
`task-3` still needs the deployed correction and an explicit request.

## Running revision: client-1 offline evidence and task-3 handoff

This append collates the available post-implementation worker evidence for
`client-1` under the existing sprint `offline-build` criterion. It does not add
scope, authorize deployment, or satisfy `operator-browser` or
`running-image-comparison`. Those two **OperatorObserved criteria remain
Pending**. The historical live observations above concern earlier releases and
must not be reused as acceptance of this running-revision feature.

### Worker results — retained UI implementation, not operator acceptance

The retained client implementation is local commit
`c4a90ff3b1de08dfc359266673e17ac8b8340f17`, tree
`bb323accab62bafec1998c14a611f96b1ddfafa5`, from client worker run
`ce6b5fd846d4`. The local `issues.jsonl` viewer's `client-1.reworkContext`
(updated 2026-11-17 at 20:46:14.835) records **95/95 targeted checks and 84/84
revision checks**, followed by the worker timeout. Its `modelResponse` is
`<timed out after 15m>`, not a completed verification receipt. These are
attributed prior-run results from the recovery summary, not a newly obtained
successful client completion summary. Their full original command set is not
available in that summary; the counts may overlap and must not be added.
The viewer is historical corroboration, not queue or completion authority.

To supply reproducible build/component/client/state evidence rather than infer
success from that summary, task-3 independently checked the **exact retained
client snapshot**, exported with `git archive` into its own ignored
`.test-temp/task-3-client-validation/source/` fixture. No active sibling worktree
or tracked UI source was changed. On 2026-11-17 with SDK **10.0.404**, these
commands ran in the exported snapshot, using only the local package cache for
restore (no GitHub/package-server requests):

```sh
dotnet clean Forge.Core/Forge.Core.csproj --configuration Release --nologo
dotnet restore tests/Forge.Tests/Forge.Tests.csproj --source /var/lib/forge/.nuget/packages -p:NuGetAudit=false --nologo
dotnet build Forge.Core/Forge.Core.csproj --configuration Release --no-restore --nologo
dotnet test tests/Forge.Tests/Forge.Tests.csproj --configuration Release --no-restore --nologo --filter 'FullyQualifiedName~BuildRevisionPresentationTests|FullyQualifiedName~BuildInfoEndpointTests|FullyQualifiedName~AppShellEndpointsTests' --logger 'trx;LogFileName=client-qualification.trx' --results-directory ../logs
```

Task-3 worker summary of actual results:

| Check | Observed offline result | Scope/limit |
|---|---|---|
| Clean application build, including Forge.UI Razor compilation | Passed; 0 warnings, 0 errors | Retained client snapshot only, not combined sibling branches |
| `BuildRevisionPresentationTests` | 84 passed | Static component rendering, fake HTTP client and shell state/effects/reducers; no browser |
| `AppShellEndpointsTests` | 5 passed | Isolated local HTTP fixtures |
| `BuildInfoEndpointTests` | 1 passed | Legacy version/framework compatibility case on this snapshot, **not** task-2's new revision HTTP coverage |
| Focused total (20:53:36–20:53:39 UTC) | 90 passed, 0 failed, 0 skipped | Not a full-suite run; compilation emitted existing test-project/analyzer warnings, including NU1510 |

The local logs and TRX are in task-3's ignored
`.test-temp/task-3-client-validation/logs/`; the per-class counts above come
from TRX result records, including theory cases. These are worker checks, not
trusted publication receipts or durable private operator evidence.

The 84 revision cases check the first seven characters, the full validated SHA,
exact Forge commit link, labeled native `details`/`summary` and anchor semantics
(no hover-only details or custom tab-order override), and `Unavailable` without
a disclosure/link for missing, legacy, inconsistent or failed metadata. They
exercise the real shared `MainLayout` at all discovered routed URIs in valid and
unavailable states with no selected project; page bodies are placeholders, so
this is shell/route coverage, not page-load or browser-layout acceptance.
Fake-client cases cover additive `sourceRevision`/`commitUrl` deserialization,
existing version/framework fields, request failures and caller cancellation.
State/effect/reducer cases select and switch Forge/another project and return
to no project: short/full SHA and commit target stay unchanged, metadata is
requested once without a project parameter, and project data cannot supply a
fallback revision. Static semantics do not prove actual keyboard or screen
readability; those observations remain operator-only below.

### Backend contract and remaining verification boundary

The sibling task-2 [backend contract](running-revision-api.md), inspected at
local commit `43c75dc5354d53e7683c736053b1857207135e98`, defines the additive
nullable JSON fields **`sourceRevision`** and **`commitUrl`** alongside unchanged
`informationalVersion` and `framework`. Its document is supplied by task-2's
separate change; this task does not create a duplicate or copy its backend code.
The contract URL is exactly
`https://github.com/Xyrces/Forge/commit/<full-sha>` using the full validated
40-character hexadecimal SHA from **only `/app/source-revision`**. Missing,
invalid or unreadable content returns HTTP 200 with both fields explicitly
null. `Unavailable` is UI text, never an API revision or inferred identity.

The task-2 worker report records a clean application/Razor build (0 warnings,
0 errors) and **67/67** focused reader/HTTP/completion/dashboard-contract tests,
including injected read failures, explicit nulls and project-independent API
responses. These are attributed backend worker results, not tests rerun on the
client snapshot or an integrated application. Refer to that report for its
commands and limitations; do not add overlapping test totals across reports.

**Still pending:** a successful final client worker/independent exact-tree
verification receipt, combined UI/backend/current-base build and relevant tests,
controller-managed publication and current-head review/CI. Subsequent client
changes require results for their actual final source; this retained-snapshot
record must not be relabeled as that verification. Task-3 supplies the authorized
documentation collation without blocking or duplicating parallel UI coding.
Worker-host independent verification and controller publication remain separate
from these in-session checks. No worker installed a browser, accessed the live
operator dashboard, performed a deployment, changed release automation, or
recorded operator acceptance for this handoff.

### Operator browser qualification — Pending (operator records only)

After **normally approved deployment**, the operator should record actual
observations here and retain supporting receipts/screenshots under the existing
private `.portHorizon/validation/` location in the integration worktree. This
procedure is not deployment approval and does not request a release-policy or
automation change. Only the operator may record OperatorObserved acceptance via
the normal evidence workflow; worker prose, synthetic fixtures and these empty
templates do not satisfy it.

For every observation record: operator identity, UTC time, actual browser/version,
viewport width **and height** in CSS pixels, zoom, route, project-selection state,
deployed workload/image/digest and qualified receipt reference, actions taken,
expected/actual result, and private evidence reference. Suggested test viewports
are **1440 × 900** desktop and **390 × 844** narrow; these are proposed widths,
**not measurements or observations**. Record the actual widths used (and any
additional failing width), rather than copying the suggestions as results.

| Operator scenario — repeat at desktop and narrow widths | Required actual observation | Result/evidence |
|---|---|---|
| Readability and shared placement | Short SHA is the first seven characters, readable without obscuring navigation/actions; expanded full SHA/link fit or wrap without clipping/overlap | Pending — no operator record |
| Labeled keyboard full-SHA access | Tab/Shift+Tab reaches the labeled revision summary with visible focus; Enter/Space opens/closes it; full 40-character SHA is available without hovering; link is keyboard reachable | Pending — no operator record |
| Exact commit link access | Inspect the actual href and use the link by keyboard; it is exactly `https://github.com/Xyrces/Forge/commit/<full-sha>` with that full SHA, not a short SHA, branch, project URL or query-modified target | Pending — no operator record |
| Dashboard routes / no-project state | Visit the deployed route inventory, including `/`, `/tasks`, `/specs`, `/sprints`, `/flow`, `/projects` and project overview; shared revision remains available across navigation and with no project selected/configured | Pending — no operator record |
| Project selection and switching | From no project, select Forge, switch to another project, then back; capture unchanged short/full SHA, commit target and API `sourceRevision`/`commitUrl`; arrange no-project coverage safely, without deleting production projects | Pending — no operator record |
| Unavailable state | Using the operator-approved safe arrangement below, `Unavailable` is readable and there is **no revision link** or stale full-SHA disclosure to tab to; no inferred version/project/checkout revision | Pending — no operator record |

For unavailable/no-project observations, first agree a safe isolated preview or
non-serving qualification instance and record its identity and exact arrangement
separately from the real deployed-image comparison. The operator may arrange
an absent/invalid/unreadable artifact or an explicit-null API response there;
record which case was actually used. Do **not** remove/edit `/app/source-revision`
in the serving workload, change its release configuration, delete real projects,
or deploy a deliberately broken image to manufacture a screenshot. Use the
normal approved access path; do not expose the unauthenticated dashboard. Workers
must not open a browser or the live dashboard to perform this procedure. If no
safe arrangement is approved/available, keep the scenario Pending. An isolated
preview can qualify unavailable presentation, not the deployed image's identity;
restore/close the arrangement and record that cleanup before concluding it.

### Operator running-image / receipt comparison — Pending

Only after normal release approval and deployment, identify the **actual serving
workload**, namespace, pod UID, container name and running image ID/immutable
digest, observation time, and the matching qualified build receipt (identifier,
location/hash, packaged source SHA and image digest). Use the receipt's packaged
source identity, which need not be a squash merge or PR-head SHA. Tie dashboard
and API observations to that same workload/image; identify all serving replicas
or use an operator-approved instance-specific observation path to avoid comparing
one replica's artifact to another's response. A mixed or unresolved identity
remains Pending, not a pass.

| Operator evidence field | Actual value / private evidence reference |
|---|---|
| Operator and UTC observation time | Pending |
| Actual serving workload / namespace / pod UID / container | Pending |
| Actual running image ID and immutable `sha256:` digest | Pending |
| Qualified build receipt ID / location / hash and matching image digest | Pending |
| Dashboard expanded full SHA (not seven-character display) | Pending |
| `/api/meta/buildinfo` full `sourceRevision` | Pending |
| `/api/meta/buildinfo` exact `commitUrl` and dashboard link href | Pending |
| `/app/source-revision` read **inside that actual running image/container**; retain raw output and trimmed full SHA | Pending |
| Qualified receipt packaged source full SHA | Pending |
| Four full SHAs equal; API URL and dashboard href both exactly the Forge prefix plus that SHA | Pending |
| Browser scenario/viewport evidence and operator's recorded decision | Pending |

The operator must compare **all four full SHAs**: dashboard details, API
`sourceRevision`, the running image's `/app/source-revision`, and qualified build
receipt. All four must agree, and the API `commitUrl` and actual dashboard href
must both be exactly `https://github.com/Xyrces/Forge/commit/<that-full-sha>`.
Record the comparison and receipt with the browser evidence in this existing
evidence location. Do not substitute a checkout, PR head, image tag alone,
healthy rollout, historical artifact match, merged tests or offline worker checks.
A mismatch/unavailable reading is a recorded finding, never permission to infer
or fabricate a match. No actual running-image comparison or feature browser
observations were supplied to this task: **operator-browser: Pending;
running-image-comparison: Pending** until the operator supplies real evidence.

### Task-3 documentation checks

`git diff --check` passed. A local Python check confirmed the entire pre-existing
report remained an unchanged prefix; only this report has a tracked diff; worker
counts match the retained-snapshot TRX (84 + 5 + 1 = 90); and the handoff contains
the required pending statuses, viewport/keyboard/unavailable scenarios, four-way
identity fields and exact API field/URL/artifact spellings. The backend contract
link target was verified in task-2's source above; it is not yet present on this
task's base and requires normal sibling integration. These documentation checks
are separate from both the retained UI checks and all pending operator evidence.


## Final qualification 2026-11-17

The UI-originated approved pilot completed engineering, routine delivery and
explicit operator acceptance. This was an **operator-assisted qualification of
normal workflows**, not an uninterrupted unattended success: the system defects
and sanctioned recovery steps above were necessary. No worker PR was hand-pushed
or hand-merged to bypass Forge's review/publication loop.

### Engineering and recovery

| Member | Normal worker PR | Merged source | Merge time (UTC) |
|---|---|---|---|
| Backend reader/API, `task-2` | [#139](https://github.com/Xyrces/Forge/pull/139) | `6ae68e0593c38b52d2b1bb1ada380adf5a0d4ecb` | 20:57:38 |
| Qualification handoff, `task-3` | [#140](https://github.com/Xyrces/Forge/pull/140) | `4a60a7c2a0e81d3154586289722a641641d4775b` | 21:12:39 |
| Global revision UI, `client-1` | [#142](https://github.com/Xyrces/Forge/pull/142) | `178e98d9f3ada6a85ca4a980495331f55a1b95a4` | 21:30:42 |

These members passed the normal trusted worker verification, current-head review,
CI and merge path. The ClientDev retry preserved its earlier implementation,
merged the configured default branch, and added real endpoint-to-UI integration
checks. Its model phase ended at 21:22:22.932; independent verification completed
at 21:27:17.4926477 against tree
`4832843575b35458003b961fc1b9ab3fe6611158`. PR #142's approved head was
`4338f339e49e932d29a1ffa59e4cb4ef6775d3bc`; CI run `44180000037` passed.
Model prose was not substituted for the trusted receipt.

System repairs remain distinct from that engineering delivery. PR #138 supplied
scoped re-groom recovery, exact prerequisite criteria, admission before slot
allocation and a grooming wake hint. The operator requested task-3 re-grooming
at 20:43:07.101; normal grooming and audit 2 admitted it with the same seven
criteria and original approval. It reused `offline-build`, without adding scope.
The operator guided ClientDev's requeue; after task-3 merged, dependency release
started ClientDev automatically at 21:12:44.903. No dependency edge was hand-edited.
PR #141 corrected future objective labels and watch cadence. PR #143 separated
serialized request bytes from native token admission without raising the $25
ceiling or resetting history. Their changes are ancestors of the final release.

### Manual repair X, then automatic pilot delivery Y

PR #144 repaired the merged-source reader and forward recovery. GitHub's
[2026 REST breaking changes](https://docs.github.com/en/rest/about-the-rest-api/breaking-changes?apiVersion=2026-03-10)
removed `merge_commit_sha`; only merged-PR reads now request the supported
2022-11-28 contract. Missing merge evidence still fails closed. GitHub currently
lists **March 10, 2028** as that contract's end of support: migrate and requalify
this reader before then, rather than removing the version pin or substituting a
PR head. See [API version support](https://docs.github.com/en/rest/about-the-rest-api/api-versions).

The manually approved controller repair **X** was
`deploy-52cd4b8a42b2432e853c25ecc00dfebc`, source
`575dc24f9e885834fcfd09eaacd31274525a30f8`, image digest
`sha256:be9ccf3ced8ff2a31f1e1c393fe546ad6c7ad84c7caebd00d803636071aa1e37`.
It started from the prior deployed controller and excluded the pilot UI. Its
independent release journal committed; it was not counted as pilot delivery.

The repaired controller then automatically selected, qualified, approved under
the project's routine policy, and released **Y**:

- Candidate: `deploy-7c406b8f1930d32458fc467fce3a7193`, requested 21:50:36.953,
  Deployed 21:53:55.559; independent release journal `committed`.
- Source: `44c0128a2c7be8f0e821b24e5d426fba454bfab9`.
- Image: `ghcr.io/xyrces/forge@sha256:5470a4269b5d0aab298b55df17f6a88285df68f1ac063daa1cd74e3b636019d5`.
- Exact-source [CI run 44183667879](https://github.com/Xyrces/Forge/actions/runs/44183667879):
  2,300 .NET tests, 44 release/installer tests and deterministic engineering E2E passed.
- [Image run 44183667896](https://github.com/Xyrces/Forge/actions/runs/44183667896),
  artifact `14371939973`, archive digest
  `sha256:7c13833bd0e7488e7993d37890542b904cc432e144744578ac1ece76d1d96b39`.

Durable source evidence records member frontier `178e98d9...`, running repair
`575dc24f...`, selected source `44c0128a...` and reason
`running-source-forward-recovery`. Verified ancestry retained both the completed
member merges and the running repair; this was not an arbitrary latest-branch
release. Completion and candidate share exact source Y and committed plan hash
`2a810274d95a46fd588228968cfa70771c3876ba518809aca3003536c217e042`.

The final serving controller was green pod `forge-green-55f4cf7c6-q52gc`, UID
`5ee7fb4a-3251-42f2-a827-c3860388b480`, container `forge`, epoch **32**.
Its configured image and actual image ID matched the receipt. Core and Forge
workload schemas remained **41**, with the same dataset
`f610a39c-50e1-4aab-8d5a-e18e10aeede9`.

### Actual operator acceptance and its limits

Codex performed operator qualification on the user's behalf and recorded both
`operator-browser` and `running-image-comparison` through the **actual completion
UI**, not direct API submissions. The stored identity is `authenticated shared dashboard operator`; it does not establish a uniquely authenticated individual. The serving dashboard's expanded full SHA,
`/api/meta/buildinfo.sourceRevision`, `/app/source-revision` read inside that
exact running container, and qualified receipt all equalled source Y. Both API
and dashboard linked exactly to
[the qualified commit](https://github.com/Xyrces/Forge/commit/44c0128a2c7be8f0e821b24e5d426fba454bfab9).
Keyboard activation opened that actual GitHub page.

Live browser checks used the in-app browser through an SSH loopback connection,
**1280×900** and **390×844** CSS-pixel viewports, scale **1**. Browser version was
not exposed by the tool and is not inferred. The short SHA and readable full-SHA
control persisted across Now, Intake, Specs, Sprints, Active tasks, Flow, Projects
and Forge overview at both widths. Tab reached the labelled disclosure, Enter
opened it, Space closed it, and Tab reached the commit link.

An isolated actual DashboardHost/UI build from exact source Y separately checked
Alpha→Forge→Alpha switching, an empty registry, and explicit-null metadata at
desktop/narrow widths. The two-project fixture also used 1280×720 before the
1280×900 inspection. Its clearly synthetic SHA was never presented as the live
artifact. Null showed Unavailable with no revision link/disclosure, and keyboard
navigation skipped it. These browser observations **do not include malformed
metadata or HTTP-failure scenarios**; those have offline test coverage only.
Production registry/artifact state was not altered for edge cases. Fixture
listeners were stopped and their absence verified.

At 21:58:38–42, the normal API and independent read-only SQL audit agreed:
completion `completion-sprint-7bf2555ec5` was **Satisfied**, bookkeeping
**Completed**, and its bound candidate **Deployed**. Both exact-plan/source-bound
operator records were present. Parent/container issue labels are not substituted
for this durable outcome; the spec remained Groomed rather than being manually
stamped Shipped.

The actual deployed-Y `--check` passed at **21:59:35 UTC** without model generation. A final mechanical audit confirmed unchanged work, budget and schema snapshots across read-only checks and matching four-way source/image identities. The completion UI showed zero outstanding sprint outcomes, required evidence satisfied and release healthy.

The final budget snapshot at **21:58:38 UTC** retained the **$25** limit:
**$4.330884 usage-estimated + $1.01 operator-reported**, zero outstanding holds,
zero uncertain requests, and **$19.659116 available after holds**.
`billingReconciled=false`: this is not a claim of provider-invoiced actual spend.

Primary private evidence is retained under the qualification workspace's
`.portHorizon/validation/routine-pilot/`: `final-accepted-live.json`,
`final-accepted-source-audit.json`, `post-release-sql.json`,
`live-browser-observations.json`, `synthetic-browser-observations.json`, operator
notes, `final-deployed-check.txt`, `final-invariants.txt`, `final-qualified-sprint.jpg`, exact-Y receipt, and live/synthetic screenshots. The earlier
`operator-unit-review/watch/` captures preserve worker, review and merge evidence.
These local evidence paths are not public repository attachments. This proves
this Forge pilot's delivered outcome with the recorded repairs and acceptance;
it does not qualify TimeLoop, arbitrary future initiatives, or every failure mode.
