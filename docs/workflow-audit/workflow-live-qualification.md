# Live workflow repair qualification

Host, account, network, and deployment identifiers in this archived record use example values. Commit hashes and PR references retain their original meaning.

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
- Controller instance/pod UID `00000000-0000-4000-8000-000000000031`
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
The later failure-retention and urgent-work changes were qualified separately
below.

## Subsequent sprint safety release

PR #128 merged as `10f430914cb19e5d07975d6223766b08f3ee1a8f`. Exact source
`7bac4caeef31b3dddfe5fe6a8873defa3b9f32d9` passed 1,837 .NET tests,
25 release/installer tests and the fake closed-loop check in CI `44101818736`;
image workflow `44102089119` also passed.

Image `ghcr.io/xyrces/forge@sha256:8d5a595d2482593cbb1395e3c52f41233c6c26848b21776e44697eb48d29cd01`
was deployed through normal operation `deploy-4c3c69372ebd4a7ebea49ae5fc256af6`.
Its journal committed, the green controller
`00000000-0000-4000-8000-000000000032` reported active/ready with admission open,
and SQL ownership advanced to epoch19. Schema remained39.

Failed work now retains its status regardless of age. Automatic active-sprint
additions require a technically groomed prerequisite with a current blocks edge
to unfinished sprint work, rechecked in the membership transaction. Priority,
blocker labels and requeue metadata alone do not interrupt the sprint. This is a
narrow correctness repair; parented prerequisite closure and outcome-based
planning remain outstanding. Existing manual operator actions are unchanged.
