# Temporary workflow schema bridge

This isolated branch is a maintenance fallback for the additive schema40-to41
transition. It uses the existing schema40 code and accepts only versions40
and41. It retains the reviewed worker network and dashboard API protections.
It must not merge into the strict schema41 runtime.

The bridge cannot enforce the new outcome plans, completion receipts or routine
deployment workflow. Keep design, grooming, sprint and merge held throughout
qualification and any rollback. Restoring it is for investigation; it does not
authorize legacy execution against schema41 or downgrade the database.

Held stage gates alone are insufficient: Product refinement is a separate
queue. Qualification must start with no Pending/Running refinement, active
engineering/worker execution or unfinished completion work. Verify those
conditions explicitly before migration. Do not ingest the pilot initiative
until the strict schema41 runtime is qualified and active.

Before live use, independently qualify this exact binary against isolated
Azure SQL schemas before and after M041, including old initiative, task,
spending and deployment reads. Verify additive migration preserves those rows.
Only then bind its immutable image digest into the target's release policy.

This branch performs no production database migration during runtime startup.
