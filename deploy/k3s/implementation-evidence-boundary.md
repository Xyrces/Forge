# Implementation evidence format: 41 → 42

Schema 42 is a persisted-format boundary, not a table-shape change. The new
`ImplementationVerified` criterion and independently reviewed existing-work proof
cannot be interpreted by the qualified schema-41 controller Y44c. Plan version 1
and historical plan hashes stay unchanged. M042 does not rewrite plans, task
metadata, completion receipts, or existing evidence.

The strict application requires exactly 42 in read-only controller readiness and
`--check`. SQL Server registers M042 for both Core and Workload profiles; SQLite
explicit initialization reaches the same version. Production standby, active
controller and worker construction continue using `initializeSchema:false`.
Only the separate authorized initializer changes the catalog version. There is
no automatic migration on activation and no database downgrade on rollback.

## Publication is not deployment qualification

`schema-upgrade.json` pins the 41→42 transition to the exact bridge B image
`ghcr.io/xyrces/forge@sha256:2aaee461fc8ce4aa97516ad2c8c576500a6fc5f448022ee19fbc06b728fe8508`.
The old 40→41 bridge is not suitable for this transition. Qualification receipts
and their scope are recorded in [the bridge history](format42-maintenance-bridge.md).
Pinning a digest is not itself permission to migrate production.

Read compatibility and write-guard qualification must precede pinning the bridge
digest in the deployable policy. The controlled real release/rollback exercise is
a later rollout qualification step; neither source review nor isolated SQL
results establish that it has happened. Do not declare the cutover complete until
the actual supervised rollout and required rollback observation are recorded.

## Required bridge source and artifact

Build an isolated bridge from the final integrated implementation, including the
new enum, exact proof validation, plan-aware completion and source qualification.
Merely allowing 42 in the old Y44c readiness check is unsafe. The bridge must:

1. Read both 41 and 42, while keeping its explicit initializer target at 41 (never
   use the bridge initializer to upgrade or downgrade production).
2. Preserve old `MergedAndVerified` semantics and consume new-kind records
   correctly on 42, including operator/deployment criteria still pending.
3. Enforce **no new-kind emission on 41** at persistence boundaries, not only in
   the grooming prompt: binding writes, committed plan writes and verified-existing
   completion admission must reject the new format before 42. Use database
   catalog checks in their existing transactions. The bridge should retain old
   grooming defaults and disable its new no-diff producer until that boundary.
4. Contain no general compatibility override, force flag, or tolerant unknown-enum
   fallback. Its accepted versions must be the explicit set `{41,42}`.

These bridge changes are a separate, reviewed artifact, **not implemented or
qualified by M042 alone**. Record its source SHA, full CI results, image digest,
receipt and exact reader/write-guard tests before filling the policy.

## Qualification and rollout order

1. Run clean builds, offline suites and deployment supervisor tests on the final
   strict source and bridge. Test strict readiness rejects 41/43, accepts 42;
   bridge accepts 41/42 and refuses 40/43; old Y44c refuses 42.
2. In fresh isolated Azure SQL Workload schemas only, reconstruct 41; compare all
   legacy record hashes before/after M042 and repeat migration idempotently. Test
   the Core profile using M042 alone (never full Core initialization against the
   shared database). Prove bridge writes reject new-kind records on 41, and prove
   bridge reads new valid/invalid/mixed existing/merged proof records correctly on
   42. Keep exact source/assembly/program hashes and cleanup-absence receipts.
3. Only after review and promotion, drain/hold the planning lanes and confirm no
   active worker or refinement. Deploy the qualified maintenance bridge on 41
   through the existing normal release path. Verify source and catalog 41.
4. Build the strict target with the qualified bridge digest in the 41→42 policy.
   Register its trusted CI receipt and use the existing schema supervisor. It
   stops writers, runs the explicit initializer, verifies catalog 42, qualifies
   the target standby and performs the ownership transition.
5. Exercise controlled post-migration activation failure in isolation: rollback
   restores the qualified bridge at catalog 42, never Y44c. Verify actual bridge
   processing of new-format evidence, not only HTTP readiness. Qualify the
   ordinary release failure path too, before declaring rollback supported.
6. Release planning only after successful target qualification. Future schema-42
   releases use the normal release path. Y44c and every strict-41 image are no
   longer valid rollback targets; their readiness rejection is intentional.

No live migration, bridge deployment, SQL rehearsal or paid request is implied by
this runbook. Completion proof remains independent worker verification plus a
separate exact-criterion review; the version stamp is not evidence of delivery.
