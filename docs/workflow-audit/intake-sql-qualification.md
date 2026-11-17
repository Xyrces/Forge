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

This proves fresh-schema query and transaction behavior on Azure SQL. It does
not yet qualify the complete UI flow, reconstructed39 upgrade, old-image read
compatibility, runtime permissions, or unattended release. Those remain separate
checks; no production schema40 migration has occurred.
