# Live K3s qualification — 17 November 2026

**The controlled Forge-first pilot is complete.** Forge produced and independently
verified a code change, automatically opened PR #124, obtained current-head CI and
reviewer approval, merged through its normal watcher, and deployed the resulting
merged-source image through its normal release API. The same qualification also
proved controller replacement and post-stop rollback from a genuine failed
activation. This was an operator-supervised pilot: explicit recovery/adoption,
gate releases, trusted CI-receipt registration and deployment approval were
performed by the operator. It does not establish fully unattended operation or
automatic receipt ingestion. The failures encountered and evidence boundaries
remain recorded below.

## Qualified identity and scope

- Current observed source: `1b733cb32fcfa97b70d4ab00736f3f985bf23986`,
  image `sha256:2932d59d30f92160de03e35e333cd2a6d79b0b33982f171a3b70f2784f4ed5fd`.
- Earlier recovery/adoption source: `f9dc0dcf6beccab3280ee11bb23dd9efbfb4d590`,
  image `sha256:c02b35edaf5e1862190e88d4d52a14e310151fc27d75ee89c8a6045b77e8e349`.
- Earlier lifecycle repair source: `1845bf46e50d0de9492a5b2f2026568d6550f5d7`,
  image `sha256:9de1cc92f44baebda0cedd99bd0f066357005f2375c5a0833b19efc78bcf983d`.
- Successful engineering runtime source: `b3ecd864bf68f24762dabd5ef56dd8feb423cc22`,
  image `sha256:7d786dadb6bddfd55c017e6ed59c0f7144a129eae9143166f898a890b8ce446b`.
- Gate wake-up source: `7b05ece4e74786caa8d4ded20d6a5016da4f3614`,
  image `sha256:ff1a8a989e1873a2404964468fe880cb7ef64b60e09bf8f07849800c18bf1b28`.
- Earlier guard image: source `f126ca87595d77b01cc999f85b0028e0b6a51b6e`,
  digest `sha256:16f098e4e0eb48db1c35aed96d2b2a5e69ab06cf003791e5b5f8876bc97a7d2e`.
- Initial/rollback proof source: `e0d144a55d2a387a7e14a607a999110d9f7cc08e`,
  image `sha256:6261013203a15f01a28190dcf5c04f7e0491a3f637e31bf98673a802245998f2`.
- Dataset: `f610a39c-50e1-4aab-8d5a-e18e10aeede9`.
- Forge branch: `codex/forge-first-k3s`, **not main**. TimeLoop is not onboarded.
- Application state: Azure SQL; cluster state: K3s embedded etcd.
- Controller, initializer and worker use distinct SQL identities. The operator's
  live SQL checks recorded schema 38 in `core` and `proj_forge`, only the Forge
  project, a $25 dataset limit, and zero budget requests before these release
  qualifications. Runtime/worker DDL and worker control-owner mutation were denied.

Initial operation `initial-forge-v3` succeeded and its installer replay was
idempotent. Normal API replacement
`deploy-550f89a29b8a46f8bd201a5015efc991` used the same qualified source/image and
completed replacement. These two outcomes and image identity are the operating
agent's session observations; the locally retained files listed below specifically
provide detailed evidence for the subsequent rollback trial.

## New-source upgrade and first-task staging

The reviewed worker-publication guard source `f126ca87595d77b01cc999f85b0028e0b6a51b6e`
was subsequently built and deployed through the normal API. The operating agent
recorded successful [CI run 44010329109](https://github.com/Xyrces/Forge/actions/runs/44010329109)
and [image workflow 44010328953](https://github.com/Xyrces/Forge/actions/runs/44010328953),
with 1,651 C# tests, 25 Python tests and the fake end-to-end harness passing.

Deployment `deploy-ddb45cf7e0324595ad339ae3496f5c39` reached `Deployed` at
**03:35:04.561 UTC**. Its persisted log says the new controller owns admission and
the dashboard route. The operating agent observed active blue pod
`6ff248e0-2b9b-49ef-b240-877a9d06b033`, SQL epoch 6. The retained record is
`.portHorizon/validation/live/guard-deployment.json`, SHA-256
`b413ffed275fc2673d8c418bc41d98a7d4f5dae344456f6c74a99a2d340e87e1`.
This establishes a different-source upgrade using a trusted build receipt; the
change was operator-developed, so it does not establish autonomous self-building.

First task `dev-1` was created through the normal API at 03:26:36 UTC. The project
vision was seeded through the normal memory API. Groom and sprint gates were
released at 03:35:30 UTC; merge and design remained held. Gate release did not
wake the schedulers: production composition used a **15-minute backstop**, despite
shorter class defaults. Groom/sprint were held again while this current-pilot
coordination defect was repaired; it was not deferred as an unrelated follow-up.

Normal API deployment `deploy-664673f03cf0484d9b83517266595ff7` installed the
gate wake-up repair (`7b05ece`) and recorded `Deployed` at **03:53:46.835 UTC** in
`.portHorizon/validation/live/wakeup-deployment.json`. The operating agent observed
green pod `a69515fe-1980-468f-b980-2bfa36f6658d`, epoch 7. After groom/sprint release
at 03:53:47 UTC, `dev-1` was groomed at 03:53:58 UTC and automatically entered solo
sprint `sprint-4535844dd5`.

The first CoreDev worker attempt `29010441c285016398718ba1d24c06c4a0ec38f0`
ran against source `7b05ece`. Session observation recorded a real plan-critic
approval with `fastPath=false` and schema/territory/LLM checks all approved.
The task snapshot `.portHorizon/validation/pilot-observation/dev-1-failed.json`
retains the plan-gate audit; `run-summary.json` beside it retains the failed-run
summary. The subsequent missing worktree was observed through read-only inspection,
not retained as a separate filesystem snapshot.
Local implementation and focused tests completed, but required verification
encountered an uninitialized SDK HOME and repeated correction work. Conservative
full-context budget reservation eventually denied another call. The run failed
with no trusted verification receipt and no PR. Automatic retry
`29a4ac84899237966a651239a96e0e9000f51783` was also denied by budget admission;
the task reached Failed. This is a failed engineering trial, not accepted work.

The retained `budget-before-exact-count.jsonl` records 26 completed model requests,
**$14.722265 conservative settled cost**, and no uncertain request rows in that
snapshot. The $25 dataset ceiling was not exceeded. Its summed historical
reservation figure is not actual spending or outstanding liability. The retained
`first-worker-failure.json` identifies the `budget_exhausted` failure and partial
transcript. These records preserve the failed trial rather than replacing it with
the later retry's outcome.

Three repairs were then built and deployed before normal task requeue:

- `81e4286`: retain failed-worker worktrees and record verification feedback
  before another correction call (10 focused tests reported). The earlier terminal
  cleanup deleted this trial's uncommitted worktree; no manual restoration or
  publication is being substituted for the workflow.
- `284b5c3`: use existing `/var/lib/forge` for HOME and DOTNET_CLI_HOME. A real
  offline Job `forge-sdk-home-qualification` passed SDK first use through both
  `sh` and `bash -lc`, recorded in `sdk-home-qualification.log`.
- `b3ecd86`: reserve against the exact finalized Responses input count, preserving
  the $25 cap, configured $10/$15 input/output rates and spending history. Session
  verification reports 44 focused tests plus independent review, and a live
  counting-only endpoint response of 51 input tokens without generation. This is
  counting-only transport evidence, separate from the paid retry described below.

Deployment `deploy-53c200deda94445b93fc585f5d72851e` reached `Deployed` at
**04:24:40.572 UTC**, recorded in `exact-count-deployment.json`. Its image receipt
under `.portHorizon/validation/image-b3ecd86/forge-image-receipt.json` identifies
successful workflow `44016183728` and its exact source/digest above. The
task was normally requeued, preserving the original failure and budget history.

## Successful engineering, verification and review

Engineering attempt `c9f10384249c73e6b4ac097b893b13f9ec44b7ef` succeeded at
**04:30:43.425 UTC**. The trusted verification receipt completed at
`2026-11-17T04:30:43.3745562Z` and binds:

- Tree SHA: `e4eb3d9b227a3f04446febe43fb98193cbaecf42`.
- Commands SHA: `5d667856af0e73d439233dc029b5a1489c086778f322434882c6b4b0baf56cd8`.

The normal controller publication path opened
**[PR #124](https://github.com/Xyrces/Forge/pull/124)** at
`04:30:50.240799 UTC`, with head
`548af659e482083bdb83900bc8882e92d722d5d1` and base
`codex/forge-first-k3s`. The worker pod was observed absent before PR publication;
the publication code enforces termination before committing/publishing, but a
precise pod-deletion timestamp was not captured and is not asserted here.

[CI run 44017698501](https://github.com/Xyrces/Forge/actions/runs/44017698501)
passed, and the reviewer approved that exact head at **04:31:52.521 UTC**. Local
receipts include `pilot-pr-open.json` (SHA-256
`4222596c4392543cb34c220b3e79046b6e5d9afeeb166c2eecd91b0f2eebe84c`)
and `pilot-before-lifecycle-fix.json` (SHA-256
`8d0c9ea3b04fa562699e50b9a3bb72cd0f4fb4dcd0584fad0564f17479e1b11c`).
The operating agent's latest ledger snapshot records **43 completed paid requests,
$24.809140 conservative settled cost, zero pending/unknown requests**, within the
unchanged $25 cap. These are upper-rate application accounting figures, **not a
provider invoice**; actual invoiced spending has not been queried.

Live qualification exposed a missing `RunCompletedDiff` lifecycle event despite
successful publication. Source `1845bf46e50d0de9492a5b2f2026568d6550f5d7` repaired
that event path and passed [CI 44019241972](https://github.com/Xyrces/Forge/actions/runs/44019241972)
and [image workflow 44019242029](https://github.com/Xyrces/Forge/actions/runs/44019242029);
the operating agent reported 1,691 C# tests, 25 Python tests and the fake end-to-end
harness passing. API deployment `deploy-94001d925e0d4b0c9ca7f004f0c09a2c` recorded
`Deployed` at **04:50:46.558 UTC**, with green controller
`0dbaa75b-73a6-48bd-9e23-c8d445ce6e9c`, epoch 9. This successful runtime replacement
did not resolve the existing task's recovery state.

Startup recovery ran before the watcher repair could adopt the published work.
The stale `AgentRunning` state plus `PrOpened` checkpoint incorrectly requeued
`dev-1`. Two duplicate engineering attempts,
`bde54349f94d6ca6b55d570d4a4bcbbaefe4af61` and
`b5665435d56417f56b85ce35b1f28ff0cad81fc7`, were denied **before paid generation**.
The task became Failed with a cleared checkpoint. PR #124 and its approved head
`548af659e482083bdb83900bc8882e92d722d5d1` remained unchanged. The post-regression
ledger still records 43 completed requests/$24.809140 conservative settled cost
and zero unknown requests; no new paid generation is attributed to these retries.

Earlier pilot configuration omitted `State.writeAuthority` and logged transitions
in shadow mode. The operating agent enabled `State.writeAuthority=true` before
the lifecycle-fix deployment. `lifecycle-startup.log` records authority-mode
rejection of an illegal `Dispatching`/`CiGreen` transition. This is evidence of
an enforced rejection, not evidence that the task lifecycle was healthy.

Startup recovery and the existing operator `adopt-pr` action required repairs;
the latter still used obsolete watch rows. No automatic clearing of Failed work
was substituted for that repair. The subsequent explicit recovery and merge are
recorded below. Historical lifecycle violations remain in the audit.

Retained local evidence under `.portHorizon/validation/live/`:
`lifecycle-deployment.json`, `lifecycle-startup.log`,
`pilot-after-lifecycle-deployment.json`, and
`budget-after-recovery-regression.jsonl`.

## Explicit adoption, watch continuity and normal merge

Recovery/adoption code `480b89b` and regression-test invariant correction
`f9dc0dcf6beccab3280ee11bb23dd9efbfb4d590` passed
[CI 44021823414](https://github.com/Xyrces/Forge/actions/runs/44021823414) and
[image workflow 44021823453](https://github.com/Xyrces/Forge/actions/runs/44021823453).
The operating agent recorded 1,719 C# tests, 25 Python tests and the fake
end-to-end harness passing. The retained `image-f9dc0dc/forge-image-receipt.json`
binds the source to image `c02b35ed…e8e349` (full digest above).

API deployment `deploy-e0975776ef57474ea4a34d47b6f8d18f` reached `Deployed` at
**05:13:58.750 UTC**, with blue controller
`bc415563-6dda-42b6-bc58-5b5b1af1095c`, epoch 10. The operator then used the normal
`adopt-pr` API, which validated PR #124 and exact head `548af659…22d5d1`, restored
`PROpen`, retained the exact-head review and historical violations, and recorded
the explicit `OperatorAdoptPr` audit event. This was authorized operator adoption,
not an automatic reset of Failed work or a manual Git merge.

A second same-image API deployment,
`deploy-feda478c50d5430b8f78488d0010d02c`, reached `Deployed` at
**05:16:07.449 UTC**, with green controller
`3974e739-23f3-418c-9bf2-da4714b5012f`, epoch 11. The existing PR watch survived and
reached `MergeReady`/`CiGreen`. All six worker-attempt records and the ledger were
byte-identical before and after replacement: 43 completed requests, $24.809140
conservative settled cost, no uncertain requests. The two retained JSONL files
both hash to `14bfe021973a3b09bfc59150963d66af0b5ea0a86f8dc859b24ee997057e5553`.
Thus watch continuity did not create further engineering attempts or paid calls.

**Evidence boundary:** startup recovery scanned zero tasks during this restart,
because the previously Failed task's assignee had been cleared. This live trial
proves adopted-watch continuity; it does not prove that the repaired startup
classification branch scanned this task. Offline regression tests cover the
missing-event/checkpoint recovery case.

After the normal merge-gate release, **Forge merged PR #124 at 05:16:40 UTC** into
`codex/forge-first-k3s`, merge SHA
`1b733cb32fcfa97b70d4ab00736f3f985bf23986`. `dev-1` became Completed/Merged at
**05:16:41.750 UTC**. All gates were held again. The resulting merged-source
[CI 44022969510](https://github.com/Xyrces/Forge/actions/runs/44022969510) and
[image workflow 44022969371](https://github.com/Xyrces/Forge/actions/runs/44022969371)
subsequently passed, and their qualified image was deployed as recorded below.

Local evidence: `recovery-deployment.json`, `pilot-adopted.json`,
`watch-restart-deployment.json`, `pilot-after-watch-restart.json`,
`budget-before-watch-restart.jsonl`, `budget-after-watch-restart.jsonl`,
`watch-restart-controller.log`, `pilot-merged.json`, and `pilot-github-merge.json`.

## Final agent-authored merged-source deployment

Both merged-source workflows passed, reporting **1,733 C# tests, 25 Python tests
and the fake end-to-end harness**. Receipt artifact `14302320890` has archive
SHA-256 `584ea6e5942d5c2f216c781e15f662f63db788a49ff70dbfd0d6222026b9d0fe`.
The locally retained `image-1b733cb/forge-image-receipt.json` binds source
`1b733cb32fcfa97b70d4ab00736f3f985bf23986` to the final image digest above and
successful image workflow `44022969371`. The operator registered that trusted
receipt and approved deployment through the existing API.

Deployment `deploy-feda0d2a9cd943c2971caabd05fa5256` reached **Deployed at
05:22:49.150 UTC**. The only controller pod is blue
`9479d581-9edf-46ed-aa44-d36def5604c6`, active/ready/admitting with SQL epoch 12.
The old green controller is absent. The operating agent verified that
`/app/source-revision` equals the exact merged source; the retained pod record's
image and imageID match its immutable digest.

Final state: `dev-1` is Completed/Merged; all four stage gates are held. The worker's
HOME follow-up `task-1` was already resolved by `284b5c3` and subsequent real worker
qualification, so the operator explicitly closed it through the normal API with
that reason. The active queue has zero Pending, zero InProgress, one Completed
and zero Failed entries. Historical failed attempts and lifecycle audit remain.

The same dataset still has its **$25 cap**, with **43 completed requests and
$24.809140 conservative settled accounting**, no reserved/unknown requests and no
additional generation from deployment. This amount includes the failed attempts;
it is not an invoice or a clean-run benchmark score. Actual invoiced cost was not
queried. Final SQL checks confirm schema 38 in both schemas, the Forge-only
feature-branch registry, the epoch-12 owner, and distinct initializer/runtime/worker
permission boundaries. Runtime retains broad application DML, including budget
audit deletion permission; worker lacks that permission and controller-owner
mutation. This is why trusted-code limitations below remain material.

Retained final evidence under `.portHorizon/validation/live/`:
`first-self-build-deployment.json`, `final-controller-pods.json`,
`final-sql-proof.jsonl`, `final-budget-and-attempts.jsonl`, and
`final-pilot-state.json`.

The browser dashboard was observed healthy through the local
`127.0.0.1:4097` SSH tunnel. Host SSH configuration restricts this user's local
forwarding to cluster Service `10.43.3.17:8080`, with `PermitListen none`; no LAN
dashboard listener was added. The observed hidden SSH process was PID 64944,
which is session-specific rather than a persistent service identity.

## Recorded post-stop rollback

Operation: `deploy-f951c95812a74f598e6200b9d52cd1d9`.
The real deployment API accepted a candidate backed by the successful CI receipt.
An operator-controlled candidate configuration set its data root to
`/proc/forge-proof-readonly`. This allowed read-only standby qualification but
caused actual application composition to fail when activation tried to create
the data directory. No fake build receipt, production escape switch or weakened
readiness check was used.

| UTC event | Recorded evidence |
|---|---|
| 03:14:58.544 | Previous controller active, ready and admitting; pod/owner `cfc2a8de-b995-4a76-8c64-94f5d6eed086`, epoch 3. |
| 03:15:03.983 | Injected candidate `21409709-0a0f-4f63-86c8-e0e4ebc16957` is standby/ready, admission closed, active runs zero; old owner remains epoch 3. |
| 03:15:32.020 | Release journal reaches `qualified`. |
| 03:15:32.874 | Observer records previous-controller pods absent. |
| 03:15:34.352 | Release journal records `old-stopped`. |
| 03:15:34.895 | Candidate log records `FileNotFoundException` for `/proc/forge-proof-readonly` during `BuildProjectBootstrap`; runtime reports activation failure with admission closed. |
| 03:15:35.630 | Journal reaches `rolling-back`. |
| 03:16:46.611 | Journal reaches `rolled-back`. |
| 03:16:47.246 | Previous green slot restored as new pod/owner `90dcf3b2-63c9-4c78-8ed3-70449b423de3`, epoch 5, active/ready/admitting. Deployment record is `DeployFailed`, with explicit completed-rollback log. |
| 03:16:47.262 | Dormant candidate configuration restored. |

`ControllerStatus.activeRuns=1` in active-controller evidence denotes the owned
runtime task, not proof of an engineering worker running during cutover. This
trial does not establish survival/adoption of an in-flight engineering Job.

Private evidence retained locally, deliberately not committed:

| Artifact under `.portHorizon/validation/live/` | SHA-256 |
|---|---|
| `forge-rollback-proof.jsonl` | `410fc7b2cb7e928c697ef2587d53d94b1d17e34eec844c8086a3c50582fbb732` |
| `forge-rollback-candidate.log` | `49491be25fa24442fadc6beb9f365c4ca5bbab0828b681363026c299a42fad92` |

The JSONL contains timestamped status snapshots, phase transitions, pod identities
and the reconciled deployment row. Candidate log line 171 records the genuine
composition exception. The evidence does not contain bearer-token values.

## What remains unproven

1. Fully unattended operation and automatic trusted CI-receipt ingestion. This
   pilot used explicit operator approvals and recovery/adoption actions.
2. In-flight worker adoption through controller replacement and crash recovery,
   including exact attempt/output identity and no duplicate accepted effects.
   Open-PR watch continuity is proven separately and does not cover this case.
3. Schema-changing upgrade/rollback. Current release support requires matching
   schema compatibility; rollback cannot undo arbitrary database migrations.
4. Whole-host-loss restoration and off-host recovery of the data-protection
   keyring, bootstrap credentials and execution artifacts. Azure SQL and GHCR
   survive independently of this node, but the shared PVC/keyring/artifacts remain
   local. Their survival or restoration has not been rehearsed. Off-host SQL and
   immutable images alone do not establish complete disaster recovery or portable
   reconstruction of the service.

The shared PVC and direct application SQL DML are a **trusted-code boundary**.
Workers do not receive initializer DDL credentials or a Kubernetes API token, but
these restrictions do not make the shared execution environment an adversarial
sandbox. The dashboard remains cluster-private and requires the operator's local
tunnel; it has no independent application authentication.

## Nonblocking observations retained for follow-up

- Closing the already-resolved, worker-filed `task-1` through the normal API
  produced the correct `Closed` outcome but also recorded `Closed+OperatorClosed`:
  the endpoint reports the lifecycle event after changing the row status when no
  prior lifecycle state exists. This audit-ordering defect does not reopen work.
- A held `MergeReady` watch repeatedly recorded `CiGreen`, causing roughly
  1.5-second self-triggered GitHub polling in the observed session. This is retained
  as follow-up evidence; no polling implementation change is included here.
- Deployment `commitSummary` was null. Session inspection attributed this to the
  existing unquoted `pretty=format:%h %s` argument passed through `ProcessStartInfo`.
  Exact commit SHA and receipt identity remained available.
- One stale antiforgery-cookie error appeared after cutover and the browser
  recovered automatically. The healthy UI observation followed that recovery.

These observations are recorded without code changes or newly enqueued work.

Benchmark [PR #123](https://github.com/Xyrces/Forge/pull/123) and dedicated GitHub
worker/approver identities are future work after cutover, as requested. See
[benchmark-next.md](benchmark-next.md). Neither is part of this qualification claim.
