# Main promotion and external review — 2026-11-17

## Source promotion

The restart and workflow work was integrated on `codex/forge-first-k3s`, but
the final pull request into `main` was omitted. At discovery, `main` was
`4ac44895c05122ff5705a32b4ceb93e60a6e72e5`; the integration branch was
`d272d0c99c6c7971864c49adcf52eb2febf3d8e6`, 195 commits ahead and zero behind.
PR #146 now presents that history for promotion. Its original head passed
2,300 C# tests, 44 deployment tests, and the offline end-to-end harness.

Use a merge commit to preserve the ancestors referenced by deployment and
completion evidence. The live controller remains on qualified source
`44c0128a2c7be8f0e821b24e5d426fba454bfab9`; opening this PR did not change it.
After promotion, the project registry, bootstrap configuration, and managed
checkout must move to `main` through a drained controller replacement. Updating
the registry alone does not refresh cached dispatch bundles or switch an
existing checkout. Do not rewrite historical completion evidence to pretend
its worker PRs originally targeted `main`.

## Antigravity review and coverage

An external review used Antigravity with `gemini-4-argon-high` against the
exact integration head in a separate checkout. The initial command-based
attempt was denied by unattended permissions. The continued pass used native
file-reading tools, with no repository edits or live-service actions.

The continued pass reached its 12-minute limit while writing its report. A
short report-only continuation, with no further investigation, recovered five
findings from the existing inspection. There was no reported quota-exhaustion
error; remaining account allowance was not measured. No second investigation
pass was started. This was a bounded review, not an exhaustive audit of the
369-file promotion. The reviewer explicitly left gaps in migration internals,
some planning agents, parts of triage persistence, the UI, and remaining
dashboard endpoints.

The worker retry finding was separately checked against the production call
paths and confirmed:

- QA and Reviewer omit a durable dispatch identity. `KubernetesAgentRunner`
  substitutes a hash of the prompt and uses it to find a stored worker attempt.
- At an unchanged PR head, a new QA or reviewer retry can produce the same
  prompt. The runner then returns the previous terminal result or rethrows its
  failure, instead of creating a new worker job.
- QA can consume its two-attempt allowance by replaying one failed execution
  and then park the task. Reviewer error retries can repeatedly replay an old
  failure when the surrounding GitHub context is unchanged.
- Existing dispatcher tests counted calls to a scripted runner; they did not
  prove that those calls created distinct Kubernetes worker attempts.

Successful same-head review deduplication and adoption of an interrupted
worker are intentional. A repair must preserve both while distinguishing
an explicitly admitted retry after a terminal outcome.

The remaining findings were also checked:

- Recovery and watchdog logic assumed no agent run could survive the controller
  process. That assumption is wrong for durable workers, including verification
  continuing after the model phase finishes. Orphan and PR-watch guards also
  needed the durable worker ownership check.
- Background review and QA callbacks were outside the joined controller
  lifetime. The release job's pod-deletion barrier mitigated ownership transfer,
  but local drain could finish while callbacks still mutated state.
- The real follow-up triage agent converted provider errors, malformed output,
  and cancellation into the same null result used for deterministic fallback.
  The coordinator consequently missed its durable review-required path.
- A no-change task could be terminal while lacking the task-owned merged PR
  required for sprint delivery. The planning barrier correctly refused to
  invent proof. The workflow lacked an evidence-backed existing-implementation
  path; explicit abandonment was available, so "permanent deadlock" overstated
  the finding. The operator approved independent verification and review of
  existing implementation on November 17. New prospective plans may use the
  explicit `ImplementationVerified` evidence kind; historical strict
  `MergedAndVerified` requirements and recorded plan hashes remain unchanged.

## Repair qualification

The retry repair passed 49 focused tests through real dispatcher, worker-store,
and worker-runner seams. Triage failure handling passed 19 composed tests using
the actual triage agent. Recovery and shutdown changes passed 98 focused tests.
Each production build was clean. Independent code checks covered the repairs.

The prospective existing-implementation producer passed 57 focused tests;
its Core evidence/source contract passed 77. The format-write guards passed
132. The combined retry/triage/recovery check passed 124 tests. These focused
counts overlap and must not be added together as unique tests.

A broader Windows run passed 2,362 of 2,374 tests. Ten failures require Unix
shell/file-mode support; two expected the retired unverified no-op completion
behavior. Those two expectations were corrected and their tests passed.
Published repair source `b605214` subsequently passed Linux CI: 2,383 C# tests,
44 deployment checks and the offline end-to-end harness.

Both empty-PR paths now preserve a nonterminal acceptance obligation. An exact
prospective plan permits retirement only after joined worker ownership and
observed PR closure, then requests fresh verification in a distinct checkout.
Old strict plans park for an operator decision. Positive prose-only closure
cannot satisfy a committed typed member. The separate routing review approved
the source; see [empty-PR verification](../empty-pr-verification.md).

The complete bridge source `7dbd11ba921ae62cad3b24bcc3c86830674c2db4` passed
[Linux CI](https://github.com/Xyrces/Forge/actions/runs/44198607806): 2,397 C#
tests, 44 deployment checks and the offline end-to-end harness. This includes
the routing changes. Strict target restoration received independent source
review and must pass its own exact-source CI before promotion. At this recorded
checkpoint, the PR remains a draft, unmerged, and no live cutover has occurred.

After the operator reported workstation interference, local builds and tests
were stopped, compiler servers and the leftover preview were shut down, and
heavy work moved to Linux/CI. The rule is now in `AGENTS.md`.

## Persisted-format compatibility

`ImplementationVerified` is a new persisted value that older schema41 binaries
cannot interpret. Keeping the database marker at41 would let an incompatible
rollback pass its health check. Format42 therefore introduces an explicit
initializer-owned version boundary without rewriting tables or historical data.
New-format task, draft-plan, committed-plan, repair, and proof writes require42.
General metadata operations cannot manufacture or replace trusted proof.

The strict target rejects schema41. The qualified bridge reads41/42, and the
upgrade policy now pins its actual immutable image. The isolated Azure SQL
checks proved write rejection before42, canonical proof admission, rollback on
audit failure, one-event concurrent/idempotent admission and cleanup. Exact-source
builds of old Y and the bridge verified the41/42/43 reader matrix and the bridge's
persisted proof reader. The actual completion coordinator kept deployment and
operator obligations pending. These host SDK10.0.302 source builds are separate
from the image CI evidence; they are not byte-equivalence claims for deployed
assemblies. Fixtures used new schemas and a temporary restricted principal,
without changing production data. The first fixture run failed during pooled
impersonation; cleanup succeeded, and the isolated fixture rerun disabled pooling.

Live controller replacement and controlled rollback on42 remain separate
qualification steps. See
[`implementation-evidence-boundary.md`](../../deploy/k3s/implementation-evidence-boundary.md).
Source promotion, this qualified upgrade, and the managed project's switch to
`main` are distinct steps; none is inferred from the others.
