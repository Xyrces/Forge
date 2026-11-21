# Empty PR verification

An empty diff shows no remaining branch changes; it does not establish that approved
criteria were implemented. Both ordinary and blocked watch paths now preserve that
distinction. Strict `MergedAndVerified` or legacy scope parks for an operator decision.

For an active, exact `ImplementationVerified` plan, the watcher first acquires the
existing QA/reviewer invocation gates without waiting. It requires all historical
engineering, QA and reviewer worker Jobs to be terminal and their pods joined. An
unavailable ownership read or failed join retains the watch and every artifact.

It then verifies the configured repository/base and exact head/base SHAs, closes the
empty PR, and observes it closed and unmerged. The owned store transaction rechecks
task, membership, latest plan and binding; archives the PR observation and prior
metadata in `empty_pr_retired`; clears live PR/review/QA references; and records
`EmptyPrRetired` through the lifecycle table. This queues verification, not delivery.

The next normal engineering dispatch uses a deterministic new branch and
`verificationCheckoutKey`, fetching the configured base into a fresh directory. The
old checkout and branch remain available as historical artifacts. Creation/reuse and
startup cleanup use `IssueWorktreeIdentity`; watch cleanup already uses the persisted
worktree path. QA's separate review checkout identities are unchanged.

A lost PR-close acknowledgement or transaction rollback leaves the task nonterminal
with its original PR association, so another watch pass can finish the same operation.
Per-task watch serialization and fresh association checks reject stale callbacks.
An interrupted process after the transaction leaves a normal Pending sprint member.

The subsequent trusted verification and independent reviewer remain mandatory. No
existing completion receipt is rewritten, no model assertion becomes proof, and
deployment/operator observation requirements remain separate. Positive prose-only
closure of any committed typed member is rejected; already proven Completed records
retain their canonical status rather than being relabeled Closed.

Qualification is offline and does not imply a live repair or deployment. Generic
database access remains a trusted-code boundary, not hostile-worker isolation.
