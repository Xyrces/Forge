# Initiative storage: actual Azure SQL qualification

The Core proposal and ingestion implementation at
`34a8e3f7aeb5ce7bffbe9e1b29a822c6201cb3e8` passed a disposable Azure SQL
rehearsal on November 17, 2026. Two unique workload schemas were created and
removed by the harness. No application schema, live initiative, task, budget
record or runtime identity was changed. No model calls were made.

The harness was independently reviewed before execution. It used only the
Workload migration profile: the historical Core/profile24 migration includes
a fixed `proj_default` path and must not run as part of an isolated rehearsal
against this database. The executable verified its source assembly revision,
read the private initializer connection file on the host, and emitted only
sanitized results.

Passed cases:

- Concurrent creation of the same source finding produced one proposal;
  concurrent expansion produced one Intake conversation and no executable work.
- Approval rejected an obsolete revision and preserved the first recorded
  operator attribution across concurrent retries.
- Concurrent ingestion produced exactly one epic, Draft spec and receipt,
  retaining the normal IssueStore creation event.
- A recreated coordinator adopted the target receipt after a simulated missing
  primary acknowledgement; acknowledgement completed without duplicate work.
- A different project store and a mutated approved payload were rejected.
- One of four concurrent refinement claims succeeded. Restart recovery retained
  interrupted work as uncertain/failed, and only explicit retry made it pending.
- Refinement completion atomically recorded completion and ReadyForDesign.
- A SQL trigger injected failure during spec insertion; the epic, spec and
  ingestion receipt all rolled back, preserving the previous row counts.
- Repeating the additive M040 DDL preserved the completed receipt and schema40.

Private source hashes, manifest and result records are retained under
`.portHorizon/validation/intake-sql/` in the integration checkout. Both
`audit_intake_6c159201308842c3aaa63aa110755476_primary` and its `_target`
counterpart were removed, with cleanup recorded.

This proves fresh-schema query and transaction behavior on Azure SQL. The
upgrade compatibility check below was performed separately.

## Reconstructed schema39 upgrade and bridge reads

The implementation at `d89f9ed42b35b13dc5c358d6b2622ce7680c5290` also passed
an isolated schema39-to40 rehearsal. The fixture was reconstructed from the
schema39 workload shape; it was not a historical production backup. The helper
used a separate compiled assembly from bridge revision
`a7c25bcd57078aedba44b46d37a94c56feed0efa` to read the fixture both before and
after migration.

Passed cases:

- The bridge verified and read schema39, then verified and read schema40 after
  additive M040 was applied twice.
- Existing issue, event, spec, version and extraction rows remained identical.
  The comparison snapshot hash was
  `d01b5c4e77d6bdd3abe9a194fdd497c5da582f4a75ba67971224fd8c00fa8461`.
- New proposal ingestion preserved the old work. Source-binding lookup using
  SQL Server `OPENJSON` respected project ownership.
- Source acknowledgement replay succeeded. A conflicting binding in a group
  rolled the entire consumption transaction back.

The disposable schema
`audit_intake_dbc86a9c27db457d912769f8534dee2c_upgrade` was removed and its
absence asserted. Exact assembly pins, manifest, source and results are under
`.portHorizon/validation/upgrade-sql/`. This check predates the subsequent
single-statement batch-consumption optimization, which was qualified separately
below.

## Bounded batch acknowledgement

The optimized query at `a4e038ebb4b5f25190b6a8241ac3fbb6964f3ec1` passed
another actual Azure SQL rehearsal: a 1,000-source group was acknowledged by
one bounded update; duplicate/replayed acknowledgements preserved timestamps;
a missing or conflicting source rolled back the whole group; and concurrent
different bindings produced exactly one winner. This exercises consumption,
not proposal-source lookup (covered by the earlier upgrade fixture).

The independently reviewed harness used only
`audit_batch_ce8b79534fd74cb9be0076c97caabb56`, removed it, and asserted its
absence. Exact executable pins and results are under
`.portHorizon/validation/batch-sql/`.

## Worker authority restriction

The permission builder at `bb8a144cf483397b0b27a21ada8cc95e973905b6` passed
an isolated Azure SQL permission rehearsal. Two temporary `WITHOUT LOGIN`
users received the baseline schema-wide DML grant. The actual generated worker
object-level denials, remapped only to the disposable schema, rejected all 12
INSERT/UPDATE/DELETE attempts across the four initiative tables. Worker SELECT
remained available, and the controller-like user's 12 writes still succeeded.

Both users and schema `audit_auth_a6700820f6f04bcea8ae4f72e8035d9d` were removed
and their absence asserted. Private exact-source, assembly and result records
are under `.portHorizon/validation/initiative-permissions/` in the schema-upgrade
checkout. This proves inherited-grant override behavior; it does not establish
the production principals' effective permissions before the initializer runs.

These are real Azure SQL query and permission checks. Complete UI flow and
unattended migration, activation and rollback remain separate checks. No
production schema40 migration has occurred as of these rehearsals.
