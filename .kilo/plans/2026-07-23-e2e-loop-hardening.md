# E2E loop hardening: CI-rework loop, then the full pipeline validation run

## Operator question (2026-07-23)

"How are we looking e2e? Are we confident we go through intake → grooming → design → sprint → work → PR review → rework until gates pass → merge?"

## Assessment (verified against code 2026-07-23)

**Solid (live-verified):**
- Intake → spec → Approve → Groom (idempotent: 409 + `?force=true`; terminal `Groomed`; structural caps).
- Sprint assembly + completion + dispatch gate (first live cycle completed 2026-07-23).
- Agent runs under minimax-m3 (gateway auth via DB secret; leaked-markup continuation, bounded ×3).
- PR open + watch sweep (15-min single sweep, ~24 calls/hr with 2 watches; stale window 24h).
- External-merge detection → task completion (the operator-merge model).
- Schedulers safe from re-run loops: DesignerScheduler candidates = `ReadyForDesign` only, ArtistScheduler = `Designed` only (success moves the spec out of the candidate set; failures retry by design).

**Never exercised live:**
- Designer path end-to-end (send-to-design → DesignerScheduler → artifacts → design board → Designed).
- Artist path. A multi-task sprint with mixed outcomes → completion → next assembly.

**Missing vs the described loop — the rework loop:**
1. **CI failure is terminal.** `PRWatcher.PollWatchOnceAsync` on `CiFailed`: task → Failed, watch → Failed, worktree removed. No agent rework, no requeue. The user's loop requires "rework until all gates pass".
2. **`GitHubService.CreatePullRequestAsync` has no existing-PR handling** (`GitHubService.cs:40` — bare `PullRequest.Create`). A reworked task pushing to the same branch would throw `ValidationException: A pull request already exists`.
3. **No failure context to the agent.** A reworked task re-dispatches with the original prompt only — the agent can't see *why* CI failed.
4. **Merge gate** = operator merge by hand (by design, solo identity; watches detect it). Accepted — not a gap.

**Answer: not yet. One feature away.** Everything up to PR-open is solid; the "rework until gates pass" stage does not exist yet. Implement R1, then run the E2E (R3).

## R1 — CI-rework loop (the core item)

Semantics: CI failure on a watched PR → bounded agent rework on the SAME branch/PR, carrying failure context; CI keeps re-running on pushes; watch keeps watching. After `MaxReworkAttempts` (3), terminal Failed for the operator.

- **PRWatcher.CiFailed path** (`Reviewer/PRWatcher.cs`):
  - Read `reworkAttempts` from task metadata. If `< MaxReworkAttempts`: increment, transition task **Failed→Pending** with metadata `ciFailure` (sha + failing check names from the commit-status call), keep the watch **Pending** (it stays on the same PR), **keep the worktree** (agent continues in place; `GitWorktreeService` already reuses existing worktrees).
  - Else: current behavior (task Failed, watch Failed, remove worktree).
  - Gate the whole branch on a `reworkEnabled` ctor flag (default true in production bundle; the e2e-harness/tests keep current semantics explicitly).
- **GitHubService**: add `GetOpenPullRequestForBranchAsync(string branch)` (Octokit `PullRequest.GetAllForRepository` filtered by head ref + open). `CommitPushPrExecutor`: when the issue already has `prNumber` metadata (rework), skip create — reuse the number; else create as today. (The dispatch's push already updated the branch; CI re-triggers from the push.)
- **RunAgentExecutor**: when task metadata has `ciFailure`, append a `## CI failure (rework)` section to the prompt: the failure summary + instruction to fix on the existing branch.
- **Tests**:
  - `PRWatcherTests`: ci-failed with attempts < 3 → task Pending, watch Pending, worktree kept, metadata bumped; attempts == 3 → terminal as today.
  - Executor: existing `prNumber` → no create call (fake GitHub records); prompt carries the rework section.
  - Keep `dotnet test` green; TreatWarningsAsErrors clean.

## R2 — Pre-flight checks for the live run

- Deploy R1; `--check` green from `~/.config/forge`.
- Operator-merges PR #7 + #8 (CI green) → confirm watch sweep completes task-8/task-10 (validates external-merge path on the new sweep) → spec detail page **Ship** → close epic-6.
- GitHub quota: sweeps are cheap; no action needed.

## R3 — The E2E validation run (UI-driven, Playwright as before)

Fresh small feature (candidate: `GET /api/meta/sprint` — returns active sprint id/name/goal/progress; small, real, UI-visible after):

1. **Intake** (`stage1-intake.mjs` pattern): submit feature → accept epic → spec Draft exists.
2. **Design**: spec detail → "Send to Designer" → assert DesignerScheduler run (journal + designer_run row) → design board shows artifact → spec `Designed`. (First live Designer run — watch `<dataRoot>/logs/agent.log`.)
3. **Groom**: detail page Groom → tree fills (stories + tasks, bounded).
4. **Sprint**: within ~5 min assert `sprint.started` event + Sprints page shows the new active sprint with tasks in To do; dispatch claims a member (kanban moves). Non-member tasks do NOT dispatch.
5. **Work → PR**: agent runs (sprint block + sprint memory visible in agent.log on the second task), PR opens, watch attaches.
6. **Rework**: if CI fails → task requeues with `ciFailure`, agent reworks, same PR updates (R1 path). If CI passes first try, validate rework separately by enqueuing a task whose change intentionally breaks a test (then watch it recover; close that PR unmerged after).
7. **Merge**: operator merges → sweep completes task → when all sprint tasks terminal, `sprint.completed` + next sprint assembles (or gate closes if nothing eligible).
8. Spec → **Ship**; epic → Closed. Screenshots at each stage into `/home/jtn5016/e2e/`.

## Out of scope

- Automated PR review (Reviewer role + token) — operator review is the designed gate.
- Artist-path live validation (no UI work in the test feature).
- Multi-project sprint assembly (single `forge` project today).

## Files (expected)

- `Reviewer/PRWatcher.cs` (rework branch), `GitHubService.cs` (find-open-PR), `Orchestrator/Workflow/CommitPushPrExecutor.cs` (reuse PR), `Orchestrator/Workflow/RunAgentExecutor.cs` (rework prompt section), `Orchestrator/ProjectDispatchBundle.cs` (flag wiring).
- Tests: `tests/Forge.Tests/PRWatcherTests.cs`, `tests/Forge.Tests/RunAgentExecutorTests.cs`, executor tests.
- `tests/Forge.Tests/Integration/SprintAssemblerTests.cs` (reworked task remains sprint member — completion still gates on it).
- `AGENTS.md` (rework loop in the sprint-flow bullet), `.kilo/skills/forge-task-lifecycle/SKILL.md` (watch lifecycle now includes rework).
