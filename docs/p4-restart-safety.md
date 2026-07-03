# P4 — restart safety + durable execution

Status: planning. Goal: an orchestrator restart (crash, Ctrl-C, host reboot, `dotnet run` re-launch) recovers in-flight work without losing it. The original P4 plan in `docs/agent-framework-design.md` assumed we'd adopt `Microsoft.Agents.AI.DurableTask`. This doc supersedes that with a two-stage plan: **(A) checkpoint-based recovery** that works on the in-process runtime we already have, and **(B) opt-in DurableTask** behind a feature flag for when we actually need cross-restart guarantees.

## Why this is needed now

P0..P3 closed; P2.b (Artist + Meshy) shipped. Two real bugs surfaced from the audit:

1. **The "reaper" doesn't exist.** `Spawner.StaleMinutes = 30` is documented in `appsettings.json` + `README.md` + `docs/vision-status.md`. Nothing reads it. An orchestrator crash mid-dispatch leaves `InProgress` issues permanently stuck; a re-launch resumes the dispatch loop but skips the half-done work, and the issue's `worktreePath` / `branchSha` / `prNumber` metadata is never inspected. This is operator-visible: "why is task-7 still InProgress 3 days later?"
2. **No boundary checkpoints.** `CommitPushPrExecutor` writes `prNumber` + `branchSha` on success, but on failure between commit and push, or push and PR-open, the metadata is unchanged from the last known good state. A recoverer can't tell which sub-step failed.

The P3 workflow is `Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch`. The recoverable boundaries are: worktree acquired; agent run completed; commit done; push done; PR opened. We already record `worktreePath` + `branchSha` + `prNumber` — what's missing is a checkpoint BEFORE each side-effect + a recovery pass at startup that reads them.

## Design principles

- **In-process recovery by default.** Stage A only. No external dependencies. The operator gets a working reaper + a `--recover` pre-flight that lists what would happen on a real restart. Stage B (DurableTask) is opt-in.
- **Checkpoints are best-effort + idempotent.** Each boundary writes a checkpoint before the side-effect. If the checkpoint fails (DB write error), the side-effect still happens; we lose the checkpoint, not the work. The recoverer treats missing checkpoints as "this side-effect hasn't happened" and replays it.
- **Recovery is synchronous + auditable.** The recoverer runs at startup BEFORE the dispatch loop starts. It produces a log (`/api/orchestrator/recover-report` + a structured `recovery_report` row) the operator can inspect. No silent re-dispatch.
- **Per-issue, not per-workflow.** We don't need DurableTask to recover a single in-flight issue. The dispatcher loop is per-issue; the workflow is per-issue; the recovery is per-issue. This is the granularity we already have.
- **Stage B is a swap of the runtime.** Stage A leaves the runtime unchanged. Stage B adds `Microsoft.Agents.AI.DurableTask` behind `Orchestrator:Execution=Durable` in appsettings. In-process runtime stays the default.

## Stage A — checkpoint-based recovery

### A.1 — Checkpoints

Add a `dispatch_checkpoint` column to `issue` (or a new `issue_checkpoint` table — see A.4). The checkpoint is one of:

| Checkpoint | Means | Side-effects already taken | Side-effects still to take |
|---|---|---|---|
| `claimed` | ClaimExecutor accepted the issue | status=InProgress, assignee=kilo | WorktreeExecutor → acquire worktree |
| `worktree_acquired` | Worktree directory exists, branch created | `worktreePath` + `branch` set | RunAgent → agent loop |
| `agent_completed` | LLM finished; `result.Text` captured | `modelResponse` set; files in worktree | CommitPushPr → commit |
| `commit_done` | Local commit on the branch | `branchSha` updated | push |
| `push_done` | Remote branch pushed | (no extra metadata) | open PR |
| `pr_opened` | PR exists on GitHub | `prNumber` set | enqueue watch |

The checkpoint is advanced BEFORE the next side-effect. On restart, the recoverer reads the latest checkpoint and resumes from the next row.

### A.2 — Recovery at startup

`Program.cs` adds a `StartupRecovery.RunAsync(specs, issues, worktrees, gitHub, events, logger)` step before the dispatch loop. It:

1. Loads every issue with `status=InProgress` + `assignee=kilo`.
2. For each: classify by metadata (no checkpoint → just-claimed; `worktreePath` set → worktree_acquired; `branchSha` set → commit_done; `prNumber` set → pr_opened).
3. For each classification, either:
   - **Replay from next checkpoint.** E.g. `commit_done` + no `prNumber` → re-push (idempotent: `git push` is a no-op if the remote is up-to-date) and re-open the PR (idempotent: re-opening a closed PR with the same head fails; we use the existing PR if `prNumber` is set, else create new).
   - **Fail it.** If the worktree directory doesn't exist when we expect it to, transition the issue to `Failed` with `lastError = "recovered: worktree missing"`.
   - **Leave alone.** `pr_opened` + no `prNumber` mismatch → leave the issue in InProgress, log a warning. (The dispatch loop will re-discover it on the next ReadyAsync tick — but `ReadyAsync` filters by `Pending`, not `InProgress`. We need to add a `RecoveredAsync` filter or transition back to Pending.)

Each transition publishes a `dispatch.recovered` dashboard event with the issue id + the recovery action taken. The dashboard's Tasks tab shows a "recovered" pill.

### A.3 — Command surface

| Command | Effect | Use |
|---|---|---|
| `--check` | Existing. Validate config + schema + auth. | Pre-flight |
| `--recover` (new) | Run `StartupRecovery` and exit 0 with a JSON report of what would be touched. **No side-effects.** | Operator dry-run |
| `--recover-and-start` (new) | Run `StartupRecovery` with side-effects, then start dispatch | The new normal startup |
| `POST /api/orchestrator/recover` (new) | Same as `--recover-and-start` from the dashboard | Manual recovery mid-session |
| `GET /api/orchestrator/recovery-report?since=...` (new) | Recent `recovery_report` rows | Operator audit |

### A.4 — Storage

The `dispatch_checkpoint` column is a 6-value enum-as-string. We add it to `issue` (SQLite schema v11):

```sql
-- v11: dispatch_checkpoint column on issue
ALTER TABLE issue ADD COLUMN dispatch_checkpoint TEXT;
ALTER TABLE issue ADD COLUMN checkpoint_at TEXT;
ALTER TABLE issue ADD COLUMN recovery_attempts INTEGER NOT NULL DEFAULT 0;

-- v11: recovery_report table. One row per startup recovery pass.
CREATE TABLE IF NOT EXISTS recovery_report (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ts              TEXT NOT NULL,
    spec_id         TEXT,           -- null = full sweep
    issues_scanned  INTEGER NOT NULL,
    issues_replayed INTEGER NOT NULL,
    issues_failed   INTEGER NOT NULL,
    actions_json    TEXT NOT NULL,  -- JSON array of {issueId, beforeCheckpoint, afterCheckpoint, action}
    duration_ms     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_recovery_report_ts ON recovery_report(ts);
```

The `recovery_report` table is the audit trail. The `dispatch_checkpoint` column is the per-issue state.

### A.5 — Tests

Recovery is the kind of thing that breaks silently. Tests at three levels:

- **Unit tests on `StartupRecovery.ClassifyAsync(issue)`**: feed it a fixture issue for each checkpoint state, assert the classification.
- **Unit tests on `StartupRecovery.ReplayFromAsync(issue, checkpoint)`**: stub the worktree + GitHub services, assert the right method was called for each replay case. (E.g. `commit_done` → `worktrees.PushAsync` then `gitHub.CreatePullRequestAsync`.)
- **Integration test on `StartupRecovery.RunAsync(...)`**: build a real IssueStore + a stubbed WorktreeService + GitHubService that records calls, set up 6 issues each in a different checkpoint state, run recovery, assert: 4 replayed correctly + 1 failed (missing worktree) + 1 left alone (PR exists, no replay needed), 1 `recovery_report` row written.

### A.6 — Schema migration + opt-in safety

Schema v11 is additive (no destructive ALTERs). The migration runs in `IssueStore.InitializeSchema` on first launch. If recovery finds issues with `dispatch_checkpoint IS NULL`, it classifies them as `claimed` and replays from worktree acquisition. This is the safest default — re-acquiring a worktree that exists is a no-op.

The `--check` flag (already implemented) should be extended to assert that schema v11 is applied. Catches the operator running an old binary against a new DB.

## Stage B — DurableTask opt-in

Behind `Orchestrator:Execution=InProcess|Durable` in appsettings.

### B.1 — Dependency

Add `<PackageReference Include="Microsoft.Agents.AI.DurableTask" Version="..." />`. The package is still being shaped; the `docs/agent-framework-design.md` "Risks" section (item 5 + 10) flags the surface as unstable. We pin a specific version and don't auto-upgrade.

### B.2 — DTS sidecar

DurableTask requires either Azure-hosted Durable Functions, the DTS emulator Docker image, or a self-hosted DTS. We don't have any of those in this repo. **Adding Stage B means adding a Docker dependency to the dev story.**

Mitigation: a `docker-compose.yml` that runs DTS emulator alongside the orchestrator for local dev. CI / prod uses Azure. We document the dependency in `docs/install-kilo.md`.

### B.3 — Workflow change

`DispatchSingleTaskAsync` becomes a Durable orchestration. The 5-executor workflow stays the same shape (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch); each executor becomes a Durable activity. The recoverer becomes a no-op (Durable Task does it for us).

PR-merge signal becomes a `WaitForExternalEvent("PR_MERGED")` raised by a GitHub App webhook instead of the 30s `PRWatcher` poll. This is a real improvement — no polling latency, no missed merges on a downed orchestrator.

### B.4 — Tests

DurableTask's `DurableTaskClient` can run in-process for tests (the `dotnet/samples/04-hosting` pattern). We add one integration test: spin up the in-process DTS, dispatch a fixture issue with a stubbed IChatClient, kill the orchestrator mid-workflow, re-launch, assert the workflow resumes from the last activity and completes.

### B.5 — Cost

DurableTask adds:
- A Docker dependency (B.2).
- A Durable Functions or DTS emulator in dev.
- A new failure mode (DTS backend down → orchestrator can't dispatch).
- A new operator learning curve (Durable Task Hub, event subscriptions).

The benefit is real (no polling, no recovery script, no half-done work) but it only pays off when we have >1 orchestrator instance or are running in a managed environment. Stage B is opt-in for now.

## Phasing + commits

### Stage A — checkpoint-based recovery (recommended now)

Multiple smaller commits per operator preference:

1. **A.1 — checkpoints** Schema v11 + `dispatch_checkpoint` + `checkpoint_at` + `recovery_attempts` + `recovery_report` table. Tests on the migration.
2. **A.2 — `StartupRecovery` service** + classification + replay logic. Stubbed unit tests for each checkpoint state.
3. **A.3 — `--recover` / `--recover-and-start` / `POST /api/orchestrator/recover`** CLI + endpoints. Operator-visible.
4. **A.4 — `recovery_report` endpoints** + dashboard Recovery tab (compact view of recent reports).
5. **A.5 — integration test** the 6-issue fixture + a real `GitWorktreeService` against a temp git repo + a stubbed GitHubService.
6. **A.6 — docs** update `vision-status.md` (P4 partial ✅), `operator-cookbook.md` (recovery runbook), `system-flow.md` (the recovery step).

Estimated: 4-6 commits, ~30 new tests, 1 week of work.

### Stage B — DurableTask

Deferred until Stage A is shipped + the operator confirms the Docker dependency is acceptable. Estimated: 2-3 weeks.

## What Stage A does NOT do

- It does **not** recover the Groomer / Designer / Artist schedulers' in-flight LLM calls. Those use fresh MAF agents per run; restart loses them. Stage A recovers per-issue dispatch only.
- It does **not** recover the `ProductRefinementQueue` mid-event-loop. The queue is a long-lived worker (held in a static field per operator preference); restart loses any event it's processing. Stage A is a no-op for it.
- It does **not** add durable agent conversation history. `AgentSession.SerializeSessionAsync` already covers that half; Stage A doesn't add anything new there.
- It does **not** make `Meshy` jobs resumable. If the orchestrator restarts mid-Meshy-poll, the Artist agent's next run re-queries the Meshy task id from `artist_run.meshy_tasks` and continues from there. That's already wired; Stage A inherits it.

## Open questions for the operator

1. **Is the Docker dependency for Stage B acceptable?** If yes, we ship Stage B. If no, Stage A is the final P4.
2. **Per-sprint vs per-issue recovery granularity.** Per-issue is what we have today. Per-sprint (resume the whole workflow from the failed issue) requires DurableTask. **Default: per-issue.**
3. **Recovery audit retention.** How long do we keep `recovery_report` rows? Default: forever (it's a small table; an issue's lifetime is bounded but the audit trail is operator-facing).
4. **Should `--recover` be the default startup mode?** I.e. always run recovery before dispatch. Default: yes (it's a 1-second pass; only kicks in if there are InProgress issues; transparent on a clean startup).

## Test strategy

- **Unit on `ClassifyAsync` / `ReplayFromAsync`** — every checkpoint state, plus the edge cases (missing worktree, remote branch diverged, PR already merged).
- **Integration on `StartupRecovery.RunAsync`** — 6 issues in different states, real `GitWorktreeService` against a temp git repo, stubbed `GitHubService`, asserts the recovery_report row contents + the per-issue state transitions.
- **Live verify** — boot the orchestrator, dispatch a fixture issue, kill the process mid-workflow, restart, observe the recovery_report row + the issue's final state.

## See also

- `docs/agent-framework-design.md` — original P4 plan + stays-vs-changes table + restart-safety section. Stage B still aligns with this; Stage A is the gap-fill.
- `docs/vision-status.md` — P4 row will flip from "not started" to "partial" when Stage A lands, "done" when Stage B lands.
- `docs/operator-cookbook.md` — recovery runbook will go in the "Watch a PR through the review loop" section.