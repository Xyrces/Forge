# Temporary Intake schema bridge

This branch is a maintenance rollback binary, not the schema40 feature release.
It retains the schema39 workflow and spending queries and accepts only database
versions39 and40. M040 adds initiative tables without changing the old table
shape. This allowance must not merge into the strict schema40 runtime.

The bridge cannot display or process schema40 initiative proposals and receipts.
Keep workflow gates held throughout migration qualification. Returning to this
image restores the previous control surface while investigating a failed upgrade;
it does not provide the new approval workflow or downgrade database state.

Before any live migration, qualify this exact image against a disposable M040
database, including existing budget reconciliation and task reads. Record its
immutable digest in the target release's reviewed upgrade policy. The prior
strict39 image is not a valid rollback target after schema40 migration.

No production database change is performed by this branch or its runtime startup.
