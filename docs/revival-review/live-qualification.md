# Live K3s qualification — 17 November 2026

The deployed architecture has completed initial startup, same-image API-driven
controller replacement, and a genuine failed activation followed by rollback
after the previous controller's pods were deleted. This is infrastructure
qualification, not yet proof that Forge can complete a paid engineering task,
build a different source revision and deploy that new revision itself.

## Qualified identity and scope

- Source: `e0d144a55d2a387a7e14a607a999110d9f7cc08e`.
- Active image digest: `sha256:6261013203a15f01a28190dcf5c04f7e0491a3f637e31bf98673a802245998f2`.
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

1. A bounded paid Forge task completing the full grooming/engineering/verification/
   review/CI path, with durable reservations and actual usage reconciled.
2. A different source commit built by the workflow, explicitly registered through
   its trusted receipt, then deployed through the API. Same-image replacement is
   meaningful ownership/restart evidence but is not a new-source upgrade.
3. In-flight worker adoption through controller replacement and crash recovery,
   including exact attempt/output identity and no duplicate accepted effects.
4. Schema-changing upgrade/rollback. Current release support requires matching
   schema compatibility; rollback cannot undo arbitrary database migrations.

The shared PVC and direct application SQL DML are a **trusted-code boundary**.
Workers do not receive initializer DDL credentials or a Kubernetes API token, but
these restrictions do not make the shared execution environment an adversarial
sandbox. The dashboard remains cluster-private and requires the operator's local
tunnel; it has no independent application authentication.

Benchmark [PR #123](https://github.com/Xyrces/Forge/pull/123) and dedicated GitHub
worker/approver identities are future work after cutover, as requested. See
[benchmark-next.md](benchmark-next.md). Neither is part of this qualification claim.
