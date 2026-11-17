# Format42 maintenance bridge

This source is bridge **B**, intended to be an ancestor of the subsequent strict
format42 release **T** and the final main promotion merge. It is not an instruction
to deploy an unqualified image.

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
validation must run on Linux/CI; no final full-suite pass is claimed here.

T must restore initializer/current version42, both M042 registrations, strict
read compatibility42 and the prospective `ImplementationVerified` default. Keep
the format guards and new readers. T's 41→42 supervisor policy must pin B's actual
qualified immutable image digest. Do not use the existing old40→41 policy/digest
for this upgrade, and never roll back42 to the old Y44c schema41-only controller.

No live operation, migration, paid model request or source push accompanies this
source change. The private synthetic SQL rehearsal is separate from actual B/Y
binary reader qualification and must not be presented as that qualification.
