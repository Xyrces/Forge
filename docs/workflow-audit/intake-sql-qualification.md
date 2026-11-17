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
single-statement batch-consumption optimization; that query requires its own
qualification.

These are real Azure SQL query checks, not full bridge-image activation or
runtime-principal permission tests. Complete UI flow and unattended migration,
activation and rollback remain separate checks. No production schema40
migration has occurred as of these rehearsals.
