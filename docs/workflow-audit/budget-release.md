# Schema 39 budget release: qualified bridge plan

Planning only, 2026-11-17. No production code or deployment changes were made for this review. Baseline is `8460dde0742640bb333c4540a255e1b7a3f4b2ce` (schema 38); schema-39 billing changes referenced below are the concurrent, uncommitted budget work. Qualification and release steps below are proposed, not accomplished.

## Decision

Use a **schema-38 bridge binary that explicitly accepts database versions 38 and 39 while retaining the old SQL query shapes**, followed by an explicit, short maintenance migration and then the schema-39 budget binary. Keep that bridge as the immediate rollback image. Do not lower the database version stamp, rewrite old budget rows, make the new binary pretend it supports 38, or broadly accept future versions.

An additive migration is structurally compatible with the old store, but the old deployed host's exact schema check makes it operationally incompatible. Ordinary blue-green release alone cannot perform this upgrade safely. A maintenance gap is necessary with the existing initializer contract.

## Exact current constraints

| Touchpoint | Actual behavior and consequence |
|---|---|
| `Orchestrator/Control/ControllerHost.cs:126-133` | `VerifySchemaAsync` requires SQL `MAX(version) == SqlServerMigrations.ExpectedVersion`. Old binary expects 38; new budget binary expects 39. |
| `ControllerHost.cs:93-103` | Both controller **status** and **activate** execute that check. `/healthz` alone is not qualification. A stopped old 38 controller cannot be reactivated on DB39. |
| `ControllerHost.cs:29-38`; `Program.cs:71-74` | Distributed `--check` uses the same verifier for registry and registered project schemas and is read-only. Legacy `Program.cs:840` has its own exact check but is not the distributed entry point. |
| `Core/Db/M039BudgetUsage.cs:8-23` | M039 adds nullable `usage_json NVARCHAR(MAX)` and `pricing_basis NVARCHAR(128)` to `core.budget_reservation`, guarded by `COL_LENGTH`, plus append-only reconciliation header/covered-request tables. It returns without DDL for non-Core profiles. No old rows or monetary values are updated. |
| `Core/IssueStore.cs:1642-1665`; `Core/Db/ISqlServerMigration.cs` | Migration registration and version stamping advance initialized registry/workload schemas to the current release version even when a workload-profile migration has no physical DDL. Both schema versions matter to `--check`. |
| Baseline `Core/Control/DurableBudgetStore.cs:56`, `:91`, `:108`, `:125` | Old reservation inserts, settlement updates, balance reads, and reservation reads name explicit old columns. Nullable extra columns are structurally tolerated. This must be demonstrated with the old binary, not inferred solely from the new migration test. |
| New `Core/Control/DurableBudgetStore.cs:97`, `:119`, `:135` | New code writes and queries the new columns. Merely allowing new binary on schema38 is unsafe. |
| `Orchestrator/Control/ControllerInitializer.cs:20-38` | Initializer holds exclusive `forge-schema-migration` application lock, then refuses a non-null dataset owner or changed dataset/budget **before DDL**. Preserve these checks. |
| `ControllerInitializer.cs:46-54`, `:98-112` | Existing dataset initialization is idempotent, validates the Forge project identity, initializes the Forge workload schema, grants existing worker DML rights, and leaves bootstrap role/gate setup alone when completion marker exists. It is presently Forge-only, not a general multi-project migrator. |
| `deploy/k3s/release.py:253-279` | Release qualifies candidate status **before** draining/stopping previous controller. A schema39 candidate on DB38 cannot qualify; current release has no migration phase. |
| `deploy/k3s/release.py:211-223` | Rollback stops candidate and starts/activates previous image. After M039 that previous image must be a qualified bridge, not the original exact38 binary. |
| `deploy/k3s/install.py:175-184` | Installer initializes only when public service selector is `slot=none`, and refuses controller pods then. With an existing blue/green selector it skips initialization. Re-running normal install is not a live migration mechanism. |
| `deploy/k3s/manifests.py:57-79` | Explicit initializer Job gets initializer SQL identity, shared config/PVC, no Kubernetes service-account token. Release Job has separate identity and should not receive DDL credentials. |

Existing schema tests intentionally reject adjacent versions (`tests/Forge.Tests/ControlStoreTests.cs:77-98`). Update tests narrowly for the bridge's tested pair; keep schema39 binary strict unless another compatibility pair is separately qualified.

## Bridge source and image requirements

Cut the bridge from the current committed schema38 runtime, independently of the unfinished schema39 budget changes. Its only required runtime behavior change is a named, compile-time schema-compatibility policy used by the distributed verifier: accept **exactly {38,39}**, reject 37,40, missing/null, malformed/uninitialized schema. Do not express this as `actual >= 38`, a configurable bypass, or `ExpectedVersion +/- 1`.

Keep `IssueStore.CurrentSchemaVersion=38`, migration registry through M038, old budget-store SQL/DTOs, old request admission/settlement behavior, and runtime `initializeSchema:false`. No M039 registration or reference to its new columns belongs in the bridge. The bridge is not the cost-accounting fix; its rollback behavior remains conservative accounting. Its `--check` should report the actual observed schema and the explicitly supported pair rather than saying “schema38” when checking39.

Generate a normal immutable image and source-bound CI receipt. Qualification must cover both SQL states before touching the live database. The image catalog must bind the bridge's real commit, not the schema39 source commit or a patched filesystem snapshot.

The budget release retains version39 and requires39. M039 is nullable additive only; no legacy liabilities are retroactively recalculated. Keep the original $25 dataset budget, immutable dataset UUID, controller/worker identity split, release ownership checks, model restrictions, and gate state.

## Minimal safe execution sequence (requires separate authorization)

1. **Qualify both images off-production.** Build/test the bridge on38 and39, and budget binary on39, using an isolated SQL Server/Azure SQL database and fixture data. Qualify rollback from the budget image to the bridge on39. Publish reviewed immutable receipts for both.
2. **Deploy bridge normally on live DB38.** Use the existing API release, standby qualification, drain, pod-deletion barrier, activation, routing, and journal. Confirm bridge is the active image and record its digest/slot/instance/epoch. Before migration, normal rollback to original38 still works.
3. **Establish an explicit maintenance window.** Close admission using the authenticated controller drain; stage holds alone do not stop active-sprint dispatch. Wait for runtime shutdown/SQL lease release, stop controller replicas, and prove old pods are actually gone. Independently verify there are no surviving worker pods or queued/running durable attempts that could mutate the DB. Do not force-clear an unknown owner or uncertain budget liability to proceed. `ControllerRuntime.DrainAsync` only requests cancellation (`:65-77`); release happens later in `RunOwnedAsync` finally (`:110-122`), so the POST response is not the completion barrier.
4. **Run the explicit schema39 initializer Job once, with retry-safe identity.** Use the schema39 immutable image, existing initializer SQL secret, immutable dataset/budget/config, and existing manifest shape. Record pre/post core and `proj_forge` versions, migration outcome, and a hash/summary proving all prior ledger rows and totals unchanged. Rerunning after an interruption must be harmless. No controller or worker receives DDL credentials.
5. **Keep the previous-slot identity available.** Do **not** change public service selector to `none` merely to trigger installer initialization: release discovers `previous` from this selector (`release.py:237-242`); losing it discards the rollback target. Retain the drained bridge Deployment/template at zero replicas and its slot, even during the temporary unavailable interval. The explicit initializer Job is separate from the installer's first-install shortcut.
6. **Start the normal budget release after migration completes.** Existing release can qualify schema39 candidate on DB39, observe prior bridge already at zero replicas, prove stop, activate, and route. Invoke it through the approved deployment operation when the API is available, or through its qualified independent release Job during the maintenance gap; the current dashboard/API itself is unavailable while all controllers are stopped. Do not claim an end-to-end API migration feature already exists.
7. **On failure, reactivate the bridge on39.** Stop candidate and prove pod deletion before reactivation. Preserve schema39 and all ledger evidence; rollback is an application rollback, not a database downgrade. If M039 or workload stamping stopped partway, rerun the guarded initializer before claiming full qualification, or explicitly validate the bridge against the observed mixed38/39 schemas. Never route to an unqualified candidate because its health endpoint is alive.
8. **After success, validate cost evidence without resetting history.** Confirm old reservations/settlements and the configured ceiling are unchanged; new rows record the new evidence fields when actual authorized work occurs. Read-only probes and fake model tests should not spend paid tokens. Maintain bridge image/receipt as the rollback floor until a later separately qualified release supersedes it.

This is a controlled migration, not zero-downtime blue-green. Avoid saying the existing installer/release already implements the sequence. The present code has no durable journal covering the maintenance/migration boundary.

## Small automation change if this must be unattended

For this upgrade, the narrow reusable improvement is one explicit **schema-upgrade operation** supervised by the independent release runner (or a separate maintenance runner under the same release lock), with persisted phases: qualified bridge / drained / writers stopped / migration started / migration complete / candidate qualified / activated / committed or rolled back. It invokes the existing initializer Job with its own DDL identity; it does not add migrations to controller startup or broaden release credentials.

Record immutable source/image pair, expected before/after schema versions, previous bridge slot/digest, dataset, and unique migration Job identity. On restart, inspect SQL and Job results before repeating actions. A failed/unknown migration blocks activation until reconciled. Preserve the current no-overlap and pod-deletion barriers. This is the minimum addition to make the maintenance sequence reviewable and retryable through one operator action; a general migration framework or Kubernetes redesign is unnecessary.

A one-time supervised migration using the existing explicit primitives is possible after approval, but it must retain receipts for the same barriers and should not be presented as completion of unattended schema-upgrade support.

## Qualification matrix and required tests

| Binary | Database | Expected result |
|---|---|---|
| Original38 | 38 | Existing behavior works |
| Original38 | 39 | Status/activate/check refuse; document why it is no longer rollback target |
| Bridge38/39 | 38 | Read-only standby/check and activation work; no DDL |
| Bridge38/39 | 39 | Same; old budget insert/read/settle shapes work; new columns remain nullable |
| Bridge38/39 | 37 or40 or missing schema | Fail closed |
| Budget39 | 38 | Fail before runtime admission; no missing-column path is reached |
| Budget39 | 39 | New usage/pricing evidence persisted; legacy ledger unchanged |

Proposed tests, not yet executed by this planning task:

- Extend read-only catalog fake tests for exact bridge set and no schema writes; keep strict budget39 variant. Exercise controller status/activate and distributed `--check`, not only a helper predicate.
- Run the **actual bridge binary** against isolated SQL39: pre-existing completed/reserved/uncertain rows, idempotent reservation retry, settle an authorized fake request, balance calculation, worker claim/receipt, ownership transfer. Verify old totals and new nullable columns. The current SQLite two-column migration fixture proves additive shape only; it does not qualify the old application or Azure SQL permissions.
- Run M039 twice on isolated SQL38; verify core DDL and both schema stamps, exact old row values, and no DDL access through controller/worker identities. Exercise interruption between nullable column additions and before workload stamping.
- Python release tests: candidate39 on38 fails before old stop; migration blocked while owner/writers exist; post-migration candidate activation failure restores bridge39, never original38; stopping candidate precedes bridge activation; resume after release-runner crash does not duplicate migration or lose prior-slot identity.
- Installer test: an existing blue/green service does not implicitly migrate. A first-install `slot=none` path is not reused to erase rollback state.
- Final disposable-host proof: bridge on38 -> maintenance migration -> budget39 -> controlled candidate activation failure -> bridge on39, with ordered journal, pod UIDs, SQL epochs, schema versions, unchanged legacy ledger snapshot, and immutable image receipts. No paid model calls are required to prove schema compatibility or rollback.

## Scope and caveats

M039's nullable columns make a bridge plausible; they do not prove every previous/future release compatible. Qualify the exact source pair. The initializer currently bootstraps only Forge; additional registered project schemas would require explicit enumeration/migration before `--check` can pass. No extra project is needed for the current Forge-first dataset, but verify the registry rather than assuming it.

This report was cross-checked with the independent deployment reviewer. No host inspection, DDL, deployment, paid API request, source patch, or compatibility relaxation was performed by this planning task.

## Subsequent scoped SQL qualification

The separately authorized rehearsal is recorded in [budget-sql-rehearsal.md](budget-sql-rehearsal.md). It passed actual Azure SQL store/migration/permission checks in a disposable schema, which was removed. The evolved M039 also adds reconciliation tables; bridge rollback deliberately ignores their operator-reported correction and therefore overholds availability conservatively. This does not constitute full image activation or production migration.
