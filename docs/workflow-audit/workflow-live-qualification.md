# Live workflow repair qualification

The dashboard performance, activation guards, and workflow presentation repairs
from PR #127 are deployed. This is targeted qualification of those changes;
the UI-originated Intake-to-delivery journey remains outstanding.

## Release evidence

- Tested source: `7100922425aa149e5d3ebdba495ae1675cbbd563`.
- PR #127 merged into `codex/forge-first-k3s` as
  `330acca12ec38ca14c7cbb3466c955aa6ba6c04d`.
- Image: `ghcr.io/xyrces/forge@sha256:7721b9852ca406b2b307328800ac27b1e4316045ed9d87407797770f17073eab`.
- CI run `44097776604`: 1,831 .NET tests, 25 deployment/installer tests,
  and the fake closed-loop check passed. Image run `44097803275` succeeded.
- Normal deployment `deploy-8d8108851a274467839542f8edb58a4c` committed
  through its independent release Job; public routing selected blue.
- Controller instance/pod UID `c2faa45a-1b59-4672-884c-35538d6c3f95`
  owned epoch 18 and reported ready, active, and admission open.
- Both Azure SQL schemas remained at 39. The previous strict-39 budget image
  remained the same-schema rollback target.

The [read-model qualification](sprint-read-model.md),
[activation qualification](activation-safety.md), and
[browser qualification](sprint-ui-qualification.md) contain the disposable
SQL and browser cases. Browser inspection found two additional defects before
release: a false empty state during first load and an old project's board
briefly rendered under the newly selected project. Both were fixed and retested.

## Live browser observations

The deployed Sprints page showed loading while project/gate data arrived, then
one historical sprint and no active sprint. It displayed one of one tasks as
completed or closed and a valid repository PR link. The Flow page reported
zero active, one completed, and one closed item, with a separately labelled
Completed / closed terminal stage. The new bounded sprint-summary endpoint
returned the matching history record.

Spending remained $1.01 operator-reported prior cost and $23.99 available under
the $25 allowance, with no outstanding holds or uncertain requests. No paid
calls were made for this release. All four planning/merge gates remained held;
no synthetic Intake, spec, or engineering work was inserted into production.

Private deployment, controller-status, budget and image receipts are retained
under `.portHorizon/validation/` in the workflow integration checkout. No new
automatic rollback fault was injected during this release.

## Remaining boundaries

This release does not yet implement outcome-based sprint selection, durable
initiative approval, recoverable completion materialization, or unattended
schema upgrades. It must not be described as passing the complete user journey.
The later failure-retention and urgent-work changes require their own release.
