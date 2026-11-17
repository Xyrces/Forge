# Format42 maintenance bridge

This document records bridge **B**, source
`7dbd11ba921ae62cad3b24bcc3c86830674c2db4`, an ancestor of the subsequent strict
format42 release **T** and the final main promotion merge. The following bridge
properties describe B, not T. It is not an instruction to deploy an unqualified image.

The initializer target and fresh database version remain41. M042 is deliberately
absent from the automatic migration lists. Read-only compatibility accepts exactly
41 and42 through `RuntimeSchemaCompatibility`, used by both controller readiness
and the ordinary preflight. No runtime activation initializes the database.

All format42 readers and independent verification semantics are present. Shared
persistence guards use the literal minimum42 independently of the bridge's
initializer target. New grooming defaults remain `MergedAndVerified`; attempts
to persist new-kind bindings, draft/committed plans or canonical proofs on41 fail.
The independent verification producer checks42 before git/network/reviewer work.
On42, the bridge can read and process new-format records without treating them as
legacy success or inventing merged PRs, operator observations or deployment proof.

Tests that exercise post-upgrade records call the explicit synthetic
`Format42Fixture.UpgradeAsync`; ordinary fresh fixtures still initialize41. Bridge
catalog tests exercise both read versions and confirm initialization itself does
not advance41. The SQL initializer's target remains41, irrespective of read
compatibility. Routine deployment receipt checks also remain41: crossing to42 is
an explicit schema-supervisor operation, not an automatic routine release.

Before using B, record exact-source Linux full offline CI, image digest and receipt,
and the isolated SQL reader/guard matrix described in
[the boundary runbook](implementation-evidence-boundary.md). A preceding local
focused suite passed123 tests; it predates final routing integration and does not
qualify this commit. The attempted Windows full suite hit missing `/bin/bash` and
was stopped when the operator requested that builds move off their PC. Final B
validation ran on Linux/CI; the later exact-source results are recorded below.

T must restore initializer/current version42, both M042 registrations, strict
read compatibility42 and the prospective `ImplementationVerified` default. Keep
the format guards and new readers. T's 41→42 supervisor policy must pin B's actual
qualified immutable image digest. Do not use the existing old40→41 policy/digest
for this upgrade, and never roll back42 to the old Y44c schema41-only controller.

No live operation, migration, paid model request or source push accompanies this
source change. The private synthetic SQL rehearsal is separate from actual B/Y
binary reader qualification and must not be presented as that qualification.

## Strict restoration

T restores the current/initializer version42, the SQLite and SQL Server migration
registrations, strict read compatibility42 and the prospective new evidence kind.
It retains all persistence guards, the early producer check, exact readers and
explicit post-upgrade test fixtures. The bridge commit remains in its history.
The supervisor policy pins B's actual digest below; restoring strict source alone
does not authorize or qualify an upgrade.

## Exact bridge artifact

- Source: `7dbd11ba921ae62cad3b24bcc3c86830674c2db4`.
- Image: `ghcr.io/xyrces/forge@sha256:2aaee461fc8ce4aa97516ad2c8c576500a6fc5f448022ee19fbc06b728fe8508`.
- Image workflow: `44198607400`; receipt artifact: `14380382867`.
- Artifact archive SHA256: `f9cd3ba6e3f83bf9998dbf1f180cb6552edd854fff39a5241d102b872dfcf4ce`.
- Downloaded receipt SHA256: `81c9272f8f282be0be435ff03c07ec1d438f36cb02fa6cd9a748c816650fc679`.
- Push CI `44198607806` and PR CI `44198615763` passed: 2,397 C# tests,
  44 Python deployment tests and the offline end-to-end harness.

## Isolated SQL reader qualification

The matrix passed against isolated `qa42_` objects, with both schemas and the
temporary principal confirmed absent after cleanup:

| Exact source | Catalog41 | Catalog42 | Catalog43 |
| --- | --- | --- | --- |
| Y `44c0128a2c7be8f0e821b24e5d426fba454bfab9` | Accepted | Rejected | Rejected |
| B `7dbd11ba921ae62cad3b24bcc3c86830674c2db4` | Accepted | Accepted | Rejected |

The separate B reader loaded the persisted completion plan and validated its
canonical existing-implementation proof. Actual completion coordination retained
`PendingEvidence` with `ReadyForDeployment=true` for the later operator and
deployment obligations. Pre42 guards, reserved proof rejection, invalid identical
engineering/reviewer identity, audit-trigger rollback and concurrent idempotent
admission passed. The migration preserved column shape and the selected legacy
row; this is not a complete historical-data migration proof.

Private receipts are under `.portHorizon/validation/format42-rehearsal/`:
`matrix-run-manifest.json`, `matrix-build.json`, `MATRIX-SCOPE.md` and
`matrix-result.jsonl` (SHA256
`38203faaed6040f0bb3d51bf538f6ddfd64a5f33a044c2abda5d055331bc8942`).
The manifest records source archives, helper hashes and isolated object names;
the result includes assembly hashes and cleanup absence.

These are exact-source archive builds using host SDK10.0.302, **not byte-identical
deployed container assemblies**. InformationalVersion pins the archive source;
image CI and digest qualification above are separate evidence. No mixed PR/existing
SQL matrix, production-principal rights, controlled live format42 rollback or
completed production cutover is claimed by this rehearsal.
