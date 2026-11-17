# Live budget schema and release qualification

Status as of 2026-11-17: schema 39 migration, bridge restoration, budget release, controlled return to the bridge, forward deployment, and the operator-reported **$1.01** correction all completed. The live dashboard shows **$23.99 available** under the unchanged **$25** allowance. PR #126 merged as `ce322597424bb79ffc87b5c83ea52bc21c566afc`; the running image retains the exact tested source `567473d` below. Transitional bridge PR #125 was closed without merging its temporary 38/39 acceptance policy into the strict-39 runtime. Its source branch and immutable image remain available for rollback.

This was supervised maintenance followed by an explicitly requested previous-image rollout. It is **not** evidence of a fault-triggered automatic rollback or a fully unattended schema upgrade. No paid model call was required for these checks.

## Preparation and compatibility boundary

The earlier [isolated Azure SQL rehearsal](budget-sql-rehearsal.md) qualified the migration, store queries, immutable reconciliation coverage, and scoped permission checks before production maintenance. The [release plan](budget-release.md) and [maintenance command plan](budget-maintenance-commands.md) describe the reviewed sequence and its limitations.

The original schema38 binary cannot reactivate on39. The qualified bridge accepts exactly38/39 and retains the old budget query shape; the budget release requires exactly39. Therefore the bridge is the rollback floor after migration. It deliberately ignores later reconciliation credit and can conservatively deny paid work after a return to that image.

| Image | Exact source | Immutable image digest |
|---|---|---|
| Bridge | `6e0a19929b123e0d55eec1539c82064a5ce1bebc` | `sha256:b0757460c090ffa0f7495c35d281ebee90622cda04d20cf88d1ab509e83eab89` |
| Budget39 | `567473d2ddb2e994aaa2b9f5f8e99ca555d5994f` | `sha256:e20cf86f2c92b9c53e5fad5c7b2c4ff453320dae7016f16666c8b7999fcaf0c6` |

Both image references use `ghcr.io/xyrces/forge`. Sources were bound to the reviewed build/test receipts; these are distinct immutable releases.

## Completed observations

1. **Bridge on38:** normal API deployment `deploy-79656777ef514c9b895e8aeb86b20ea2` committed. Public routing selected green; controller instance/pod UID `87f27f37-eee0-4394-8394-3930f92dec5c` owned epoch13, with the prior blue pod absent.
2. **Writer stop before migration:** the supervised drain and stop proved SQL owner null, zero controller pods, zero worker pods, and zero active durable worker attempts. Public routing retained its existing slot rather than being reset to an installation sentinel. This proof was required separately from a successful drain request.
3. **Schema39 initialization:** completed at `2026-11-17T13:25:24Z`. Both `core` and `proj_forge` reported39. The post-migration stopped-writer receipt still showed owner null and no controller/worker pods or active worker attempts. The original43 budget rows retained raw total **$24.809140**; migration did not apply the proposed $1.01 correction or rewrite those rows.
4. **Bridge restored on39:** independent release Job operation `budget39-20261117-restore` committed through the existing release journal. Blue instance/pod UID `ea45653d-e9f8-4c21-a628-09d4f6899700` became active at epoch14. This restored the dashboard/API before requesting the new image. It was an independent release Job during the maintenance outage, not an API-recorded deployment candidate.
5. **Budget39 first activation:** normal API deployment `deploy-59de2fc56b9d46dd800c0aa33fa38360` committed. Green instance/pod UID `6a7bd7ca-e68a-4948-8256-484f48557576` became active at epoch15, using the budget digest above.

## Live permission verification

After initialization, a separate read-only harness opened the existing private connection files and queried `HAS_PERMS_BY_NAME`; it issued no SQL DML or DDL and printed no connection strings. Both `core.budget_reconciliation` and `core.budget_reconciliation_request` produced the same results:

| Identity | SELECT | INSERT | UPDATE | DELETE | Database CREATE TABLE | Object ALTER |
|---|---:|---:|---:|---:|---:|---:|
| Controller runtime | 1 | 0 | 0 | 0 | 0 | 0 |
| Worker runtime | 1 | 0 | 0 | 0 | 0 | 0 |
| Initializer/operator | 1 | 1 | 1 | 1 | 1 | 1 |

The privileged initializer remains capable of administration; append-only runtime enforcement does not claim that an administrator lacks write permissions. The production initializer applied SELECT plus explicit write DENYs to the two runtime identities. Actual denied-write behavior was separately exercised under disposable principals in the isolated rehearsal.

## Controlled return, forward release and correction

- Previous-image deployment `deploy-a288ae6677d5459ea2729b34a59ecd5a` committed through the normal API. Blue instance `59432f40-e925-4e64-ae68-22282ba0a849` owned epoch 16. Both schemas remained 39; original 43 rows and raw total were unchanged; no worker pods or queued/running attempts existed.
- Forward deployment `deploy-09f94c065c92487b98de7be84383922d` committed through the normal API. Green instance `1e174da2-f22f-4c28-b904-427ac706c44c` owned epoch 17, with admission open and the qualified budget image active. This is the final runtime for this qualification.
- The operator helper first performed a read-only dry run, matching all 43 completed request IDs and original amounts against manifest SHA256 `54a97d1f234dbf2e5c7fe60acae6e7bd9e1dc68ac759afde63be848d37f4c484`. It then appended reconciliation `pilot-2026-11-17` using the initializer identity. The recorded source is **operator-reported**, not independently retrieved OpenAI billing. Original rows were not rewritten; the immutable covered amount is $24.809140 and the reported replacement aggregate is $1.01.
- Live API readback showed limit $25, operator-reported cost $1.01, usage estimate $0, conservative uncovered amounts $0, outstanding holds $0, uncertain requests 0, and available $23.99. No paid calls were made during this qualification.
- Actual browser inspection of `/now` showed the same balance and attribution. Clicking **Refresh spending** advanced the timestamp and preserved the values. `/deployments` rendered all 13 historical candidates without the prior enum-deserialization error, including both requested transitions as Deployed. This is targeted UI qualification, not a UI-originated intake-to-delivery test.

A deliberate API rollout to an older qualified image proves that requested transition, not automatic recovery from a failed activation. Fault-triggered rollback at schema 38 was qualified in the preceding Forge-first pilot; it was not repeated as a new schema 39 fault-injection test here.

## Evidence location and limits

Private receipts are retained under `.portHorizon/validation/budget-release-live/`: bridge, rollback and forward journals/status; writer-stop proofs; initializer log; reconciliation permissions; candidate and approval receipts; `correction-dry-run.json`, `correction-applied.json`, and `corrected-api-budget.json`. This document summarizes observations rather than embedding private logs or configuration.

The maintenance sequence still requires supervised coordination across drain, initialization and bridge restoration; there is no single persisted maintenance operation spanning all three. Existing release journals supervise their individual release Jobs. Production images were activated against the migrated database, extending the earlier scoped-store rehearsal without claiming an unattended migration capability.
