# Budget schema bridge (38/39)

This branch is a narrow bridge based on `8460dde0742640bb333c4540a255e1b7a3f4b2ce`. It retains schema-38 stores, migrations, budget admission and settlement. Its distributed controller verifier accepts **exactly database versions 38 and 39**, rejects missing/37/40 versions, and reports the observed registry version in `--check`. It does not migrate to 39 and does not implement usage-based billing.

## Runtime boundary

`ControllerHost.VerifySchemaAsync` is shared by authenticated status, activation, and distributed `--check`. Status/activation validate the registry; distributed `--check` additionally validates every registered project's schema. The legacy CLI check retains its original exact-version policy; K3s selects the distributed path in `Program.cs:71-74`.

`ControllerHost.RunAsync` sets `Db.AutoMigrate=false`. Registry/workload factories receive that setting via `ForgeComposition` and `Program.FactoryFor`; `IssueStore.InitializeSchema` returns immediately for a runtime factory. `ProjectContext.EnsureSchema` therefore performs no DDL and has no hidden exact-version check blocking the bridge on39. Run the distributed check against all registered schemas as part of qualification; status alone does not validate every workload schema. Do not run this bridge's initializer as a downgrade or migration tool.

M039 in the separate budget release adds only nullable `core.budget_reservation.usage_json` and `pricing_basis`; all old SQL names explicit columns. Both registry/workload version stamps advance in that release. The bridge never imports M039 or queries those columns.

## Local validation completed

- Audit-enabled production Release build: `dotnet build Forge.Core/Forge.Core.csproj -c Release --verbosity quiet`: 0 warnings, 0 errors.
- Audit-enabled focused tests: `dotnet test tests/Forge.Tests/Forge.Tests.csproj --filter "FullyQualifiedName~BudgetBridgeCompatibilityTests|FullyQualifiedName~ControlStoreTests|FullyQualifiedName~ControllerRuntimeTests" --verbosity quiet`: **24/24 passed**.
- Read-only catalog tests accept38/39, reject37/40 and an empty version table, and prove the version is unchanged after checking.
- Actual unchanged budget-store tests on38 and the additive39 shape preserve completed spend and uncertain liability; exercise idempotent replay, new insert/settlement, ceiling refusal, owner acquire/release; preserve existing extra-column evidence and leave new legacy-writer evidence null. They assert both application schema constants remain38.

These are offline SQLite fixtures and runtime tests, not Azure SQL or deployed-image qualification. No paid requests, live SQL writes, deployments, or pushes were performed for this bridge preparation.

## Required qualification before deployment

Build an immutable image with an exact source receipt, run full Linux offline tests, and test the actual bridge image against isolated SQL Server/Azure SQL38 and39 with runtime DML-only identities. Verify read-only checks/status, activation, old budget queries, ownership/worker operations, and mixed registry/workload stamps where appropriate. Confirm37/40 rejection. Existing original38 must fail on39; budget39 must fail on38.

The image workflow automatically builds only `codex/forge-first-k3s`; this isolated branch needs an authorized `workflow_dispatch` with its exact source SHA and pinned SDK digest, or a reviewed workflow change. Do not relabel another commit's receipt as the bridge.

## Release sequence (separate approval/operation)

1. Deploy qualified bridge normally while DB remains38. Record immutable digest, slot, owner epoch, and successful full read-only check.
2. Explicitly drain, wait for SQL owner release, stop controllers, and prove all controller/worker writers stopped. Gate holds alone do not drain active work.
3. Run the **new schema39 image's initializer**, with the dedicated initializer SQL identity, same dataset/budget, no owner, and the migration lock. Preserve old monetary rows. Verify registry and project versions.
4. Preserve the public Service's previous bridge slot and its zero-replica Deployment as rollback identity. Do not set slot=none to trick the installer's first-install migration branch.
5. Run the normal independent release Job for the budget39 image on DB39. Candidate failure must stop candidate before activating the bridge on39. Never roll back to the original exact38 binary or downgrade SQL stamps.

The existing release qualifies candidate before stopping old, and has no migration phase. The initializer rejects active ownership. Consequently this is an explicit maintenance gap, not a zero-downtime migration and not an already implemented unattended schema upgrade. A durable maintenance operation should journal drain/stop/migrate/activate and reuse existing fencing/initializer identities if one-action unattended migration is required.
