# Isolated Azure SQL budget rehearsal

Completed 2026-11-17 after explicit authorization for one disposable schema and temporary contained test users. **This qualifies scoped store queries, migration and permissions on actual Azure SQL. It is not full image activation, production migration, or live reconciliation.**

Only schema `audit_budget_af24b748350546dab00a4acd7b21471e` was created. A strict `^audit_budget_[a-f0-9]{32}$` guard rejected other schema names, the schema was proved absent before creation, and every store received its explicit qualifier with `ForgeSchemaProfile.Core` and `initializeSchema:false`. `ControlDb.ValidateFactory` checks Core profile, not a literal `core` name, so no production-seam relaxation was needed. No new cloud resource was created.

The rehearsal used the existing initializer connection privately from its protected file. Credentials were not printed or copied to the source/receipt files. The test did not invoke `ControllerInitializer` or full `ControllerHost.CheckAsync`, because those target production registry/project schemas. It called the actual compiled store/migration classes and the qualifier-aware `ControllerHost.VerifySchemaAsync` directly.

## Results

| Phase | Observed result |
|---|---|
| Old bridge on SQL38 | Created 43 synthetic completed rows totaling $24.80914 plus a separate $0.05 uncertain liability. Old store read exactly those amounts. Bridge schema check accepted38. |
| M039 on SQL39 | Applied actual `M039BudgetUsage.Up` twice successfully, including both nullable evidence columns and reconciliation tables. Strict new schema check accepted39. |
| Operator-reported reconciliation | Actual `ReconcileOperatorReportedAsync` covered exactly43 fixture request IDs, recorded reported total $1.01, and retained every original monetary row ($24.80914 summed unchanged). Source remains `operator-reported`, not provider-verified billing. |
| Idempotency and exclusions | Repeating identical reconciliation returned existing rather than writing another; overlapping coverage was rejected; an uncertain request could not be included. |
| New settlement | New $0.10 request admitted and settled with usage evidence. Snapshot: $1.11 spent ($1.01 operator-reported + $0.10 usage estimate), $0.05 outstanding, $24.80914 reconciled-original audit amount, unchanged $25 ceiling. |
| Old bridge on upgraded SQL39 | Same old store/schema verifier accepted39 and read $24.90914 original spend plus $0.05 outstanding; ignored reconciliation credit conservatively and refused another $0.10 request. Rollback can therefore overhold availability but cannot double-spend credit. |
| SQL runtime permission proof | Created two temporary users WITHOUT LOGIN, granted DML only on the disposable schema, then used the actual initializer permission builder with its `[core]` object qualifier replaced by the validated audit qualifier. Under `EXECUTE AS USER`, SELECT on both reconciliation tables succeeded; all 12 actual INSERT/UPDATE/DELETE attempts (2 tables x 2 users x 3 operations) failed with permission-denied errors. Normal budget-reservation INSERT permission remained available. |
| Cleanup | Both temporary users were dropped. The harness compared actual table names with its exact seven-table allowlist before dropping those tables and the uniquely named schema. Final queries proved schema and users absent. |

Temporary principal names were `audit_rt_b00a4acd7b21471e` and `audit_wk_b00a4acd7b21471e`. No runtime/worker/initializer production-principal grant or denial was changed. Object-level **DENY**, not merely REVOKE, overrode inherited schema DML in the test, which is the important property for the production controller's existing permissions.

## Production unchanged checks

The existing read-only SQL proof helper ran before and after. Both observed:

- `core` and `proj_forge` version38.
- Dataset `f610a39c-50e1-4aab-8d5a-e18e10aeede9`, ceiling25,000,000 microUSD, owner `9479d581-9edf-46ed-aa44-d36def5604c6`, epoch12.
- 43 request rows totaling24,809,140 microUSD liability.
- The same runtime/worker/initializer permission probe results.

These checks are specific version/count/total/owner/permission comparisons, not a claim of a byte-for-byte backup of every production table. Production SQL was read-only throughout the rehearsal. No paid model requests were made.

## Source and receipts

- Old bridge runtime assembly was built from `3edec460ec0c08ccc56b27aa9c6440383332806d`; later bridge commit `6e0a19929b123e0d55eec1539c82064a5ce1bebc` changes explanatory comment/docs and adds the reconciliation fixture, without changing executable bridge behavior.
- New assembly came from the concurrent shared M039/reconciliation worktree. Its hash, not a claimed nonexistent image receipt, identifies the tested build. Requalify if the implementation changes materially before release.
- Local receipts and auditable harness source: `.portHorizon/sql-budget-rehearsal/manifest.json`, `old38.jsonl`, `new39.jsonl`, `old39.jsonl`, `cleanup.jsonl`, `production-after.jsonl`, `old/Program.cs`, `new/Program.cs`. These are local ignored operational artifacts, not secrets.
- Linux harness copy retained under `/tmp/forge-budget-rehearsal-audit_budget_af24b748350546dab00a4acd7b21471e`; it contains only compiled application/harness files and schema name, no credentials.
- Shared initializer change is confined to explicit `Bootstrap.ControllerPrincipal` plus validated pre-DDL permission SQL, and six focused permission-builder tests passed. The production initializer configuration still needs the actual controller identity `forge-k3s-runtime` before authorized migration. Worker identity remains `forge-k3s-worker`.

Full image CI, source-bound image receipts, bridge deployment, writer drain/stop, migration operation and post-migration rollback proof remain separate release steps. This rehearsal does not authorize or perform any of them.
