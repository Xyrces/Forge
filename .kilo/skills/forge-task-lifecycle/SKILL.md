---
name: forge-task-lifecycle
description: The Forge engineering dispatch pipeline — Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch + the PRWatcher lifecycle that follows. Use when reasoning about a single task's trip through the orchestrator, why a task is in a given state, or what each MAF Workflow executor does.
---

# forge-task-lifecycle

The path a single engineering task takes, end to end, in the production code path.

## The five-stage pipeline (production path)

```
[Claim] → [Worktree] → [RunAgent] → [CommitPushPr] → [EnqueueWatch] → [PRWatcher]
```

Production is `OrchestratorAgent.DispatchSingleTaskAsync` (`Orchestrator/OrchestratorAgent.cs`), which claims the issue up-front and then hands it to `IWorkflowDispatcher.DispatchAsync`. The default dispatcher is `InProcessDispatcher`, which builds `Orchestrator/Workflow/EngineeringDispatchWorkflow` per dispatch and runs it via MAF `InProcessExecution` — **the workflow executors ARE the production path**. With `Orchestrator:Execution=Durable`, the same workflow runs on the DTS sidecar (P4 Stage B — see `forge-recovery`).

Multi-project note: `OrchestratorAgent` iterates the registered projects each cycle and claims from each project's own `IssueStore` (via cached `ProjectDispatchBundle`s). The `InProcessDispatcher` builds the workflow per dispatch from the task's bundle (`Program.cs`), so non-primary projects dispatch against their own repo/stores. The legacy `DurableDispatcher` path remains startup-store-bound (known gap).

## Sprint gate (fundamental)

ALL engineering work happens inside a sprint. `Orchestrator/Sprint/SprintAssembler.cs` (5-min tick per project) completes the Active sprint when every member task is terminal, then assembles + activates the next sprint from eligible Pending tasks: grouped by groomed spec (task→story→spec parent walk) FIFO, ad-hoc parentless tasks last ("Ad-hoc work" group), containers (`epic`/`story`) and `pr-watch` never ingested, already-sprinted tasks never re-ingested. Stories are linked for progress display but don't gate completion.

`OrchestratorAgent` gates dispatch: no Active sprint → no dev dispatch; otherwise only sprint members (`SprintStore.GetIssueIdsAsync`) pass the filter. The watch sweep is STATE-DRIVEN (since 2026-07-29): it polls every live (Pending|InProgress) task carrying `prNumber` metadata, regardless of sprint membership — there are no `pr-watch` rows anymore. `StartupRecovery` is unaffected (in-flight items requeue regardless of membership).

Agent runs inside a sprint carry shared context: `RunAgentExecutor` adds `sprintId/sprintName/sprintGoal/sprintRoster` to the run context; `MafAgentRunner` renders a `## Sprint` block + recalls `sprint/{sprintId}/` memory keys (`## Sprint memory`) before global project memory. `MemoryExtractor` dual-persists extracted memories under `sprint/{sprintId}/` when the issue is in the Active sprint.

## Stage 1 — Claim

`OrchestratorAgent.DispatchSingleTaskAsync` calls `IssueStore.ClaimAsync(issue.Id, "forge", ct)`:

- Atomic: `Pending` → `InProgress`, `assignee="forge"`.
- If `null` comes back, the issue was already claimed elsewhere (by another dispatch cycle or another process) — debug-log and return `Result(false, "already-claimed")`.
- After claim, re-fetch the issue (`IssueStore.GetAsync`) so the workflow's input has the post-claim row.
- Publish `DashboardEvent` of kind `TaskTransition` for the SSE stream.

The dispatcher's `ClaimExecutor` short-circuits when the input is already `InProgress + assignee=forge` (P3 final-wiring behavior) and otherwise claims itself. Production path claims up-front and lets the dispatcher's executor pass through.

## Stage 2 — Worktree

`Orchestrator/Workflow/WorktreeExecutor.cs` (or the corresponding section of `DispatchSingleTaskAsync` for the sequential path):

- Creates branch `agent/<id>` from the workspace's `defaultBranch` (e.g. `main`).
- Creates the worktree at `<workspace.worktreeRoot>/<id>/` — default `.portHorizon/worktrees/<id>`.
- Sets metadata: `worktreePath`, `branch`.
- Advances `dispatch_checkpoint` to `worktree_acquired` **before** the worktree exists on disk.

## Stage 3 — RunAgent

`MafAgentRunner.RunAsync` builds and runs the MAF agent:

- **System instructions** are assembled in this order:
  1. **Role instructions** — `description:` frontmatter from `<workspace>/agents/<role>.md` (where `<role>` is `RoleAgent.AgentName`, e.g. `coredev`). MAF runner resolves the prompts directory via the `rolePromptsRoot` constructor parameter (default `<workspace>/agents`). Parser handles only the minimal `description:` field; multi-line YAML is out of scope per `MafAgentRunner.LoadRoleInstructions`.
  2. **Project skills block** — appended via `BuildSkillInstructionsAsync` from `ISkillSource` (currently `SqliteSkillSource`); falls back to empty on load error.
  3. **Project memory block** — `## Project memory` from `MemoryStore.RecallAsync()`; rendered as a bullet list with expiry metadata. Falls back to empty on error.
- The user's prompt (the operator's task body + worktree context) goes to the user message — **never** to instructions. This is the P1 fix.
- **Tools wired into the agent:**
  - `BashTool(workingDirectory=<worktree>, envVars=<project secrets>)` AIFunction — `/bin/sh -c <command>` on Linux/macOS, `cmd.exe /c` on Windows; default `workingDirectory` is the task's worktree. Resolved via `context["worktreePath"]`.
  - `ArtifactReadTool` — when stores are wired; lets agents pull a single artifact body on demand rather than have the orchestrator inline every body.
- **Secrets by reference:** when `context["projectId"]` is set (the workflow passes the dispatch project's id), `MafAgentRunner.ResolveSecretEnvAsync` decrypts the project's stored secrets and injects them into the bash process environment: every kind as `FORGE_SECRET_<KIND>` (uppercased, `-`→`_`), plus `github_token` as the conventional `GITHUB_TOKEN`. Values never enter the model's prompt, tool-call JSON, or logs — the model references `$VAR` names only. Role prompts document the contract.
- Optional params on AIFunctions need C# default values (`string? param = null`, not `string? param`) — the MAF binder throws `ArgumentException` otherwise.
- LLM client built via `IChatClientFactory.Create(_config, role)` — resolves the provider/model from `LlmConfig.Resolve(role)`.
- Wrapped with `ChatClientBuilder.UseFunctionInvocation()` so model-emitted `FunctionCallContent` actually runs the tool (instead of just appearing in the response).
- After the run: capture the assistant text into `modelResponse` metadata (truncated to 2000 chars). Returns an `AgentRunResult { Text, SessionId, InputTokens, OutputTokens, Elapsed }`.
- Advances `dispatch_checkpoint` to `agent_completed` after `modelResponse` is captured.

## Stage 4 — CommitPushPr

`Orchestrator/Workflow/CommitPushPrExecutor.cs`:

- `GitWorktreeService.CommitAllAsync` — commits everything in the worktree with message `Task(<id>): <title>`.
- **NoDiff short-circuit:** if no files changed, transition the issue to `Completed` with message `"no changes (agent made 0 edits)"` and return without pushing / opening a PR. The watch issue is not enqueued.
- `GitWorktreeService.PushAsync` — pushes `agent/<id>` to origin.
- `GitHubService.CreatePullRequestAsync` — opens the PR (`[type] title`, body from `BuildPrBody`).
- Sets metadata: `prNumber`, `branchSha`. Transitions issue to `Completed`.
- Advances checkpoint through `commit_done` → `push_done` → `pr_opened`.

## Stage 5 — EnqueueWatch (graph placeholder)

The `EnqueueWatch` executor remains so the (editable) workflow DAG keeps its shape, but it creates NOTHING: the task itself is the watch. `CommitPushPrExecutor` records `prNumber`/`branch`/`worktreePath`/`prOpenedAt` on the task; the sweep discovers watched tasks from that metadata. Legacy `pr-watch` rows are closed by the sweep as "superseded".
- Metadata: `prNumber`, `branch`, `worktreePath`, `taskId=<devIssueId>`.
- Title: `Watch PR #<n> for <devIssueId>`.
- The orchestrator's next dispatch cycle sees it in `ReadyAsync` and hands it to `PRWatcher.ProcessWatchTaskAsync`.

## PRWatcher — review loop, rework, merge

The sequential sweep (`OrchestratorAgent.RunWatchSweepAsync`, every 15 min; NOT a per-watch poll loop) runs **review-then-poll** per watch:

1. **Review** (`Reviewer/ReviewerDispatcher.cs::ReviewOnceAsync`): fetches the PR diff, runs the Reviewer role, records the verdict in watch metadata (`reviewSha`/`reviewVerdict`/`reviewNotes`/`reviewRound` — the machine record), posts a GitHub comment (the audit). Per-head-SHA dedupe; `Error` verdicts retry next sweep. Formal review submission is opportunistic (solo-identity 422 tolerated — the local verdict is authoritative).
2. **Poll** (`Reviewer/PRWatcher.cs::PollWatchOnceAsync`): reads CI from **check runs** (legacy combined statuses don't see GitHub Actions) and merges when: CI green AND (formal Approved review OR reviewer-agent `Approve` at the current head). External merge (operator) also closes the loop.
3. **Rework loop**: CI failure or changes-requested → task back to `Pending` with `reworkAttempts`/`reworkContext` metadata (the agent prompt surfaces it as "## Rework required"), the task stays watched, worktree kept, `reworkForSha` prevents re-triggering on the same head. Circuit breaker at `PRWatcher.MaxReworkAttempts` (3) → terminal `Failed` (CI) / `Blocked` (review) for the operator — a breaker trip is a TASK outcome; nothing else goes terminal alongside it. Reviewer-error also breaks to `Blocked` (manual review). The reworked task pushes to the SAME branch — `CommitPushPrExecutor` reuses the existing PR.

## Retry / failure semantics

`OrchestratorAgent.HandleFailureAsync`:

- `_maxRetryCount = 1`. If the workflow dispatcher threw, transition to `Pending` with `retryCount+1` and log a warning — the next dispatch cycle picks it up.
- After the retry: hard-fail to `Failed`. If a worktree path exists, `GitWorktreeService.RemoveAsync` is called best-effort.
- `OperationCanceledException` → `Failed("cancelled")` and exit.

## The two dispatcher runtimes

| Aspect | InProcess (default) | Durable / DTS (opt-in) |
|---|---|---|
| Source | `InProcessDispatcher` lambda builds `EngineeringDispatchWorkflow` per dispatch | `DurableDispatcher` registers the same workflow with the DTS sidecar |
| Crash safety | P4 Stage A `StartupRecovery` replays checkpoints at startup | Workflow state persists in the sidecar |
| Enabled by | default | `Orchestrator:Execution=Durable` |

Both run the identical five executors; the externally observable state transitions are the same. See `forge-recovery` for the full tradeoff table.

## Why this matters

This is the only flow where multiple subsystems meet: `IssueStore` (claim), git (worktree + branch), the LLM (MAF agent), GitHub (PR), and `IssueStore` again (the state-driven watch on the task's own metadata). When reasoning about "why is this task in state X" or "why didn't the agent's changes ship", walk through these stages in order — most bugs are at a stage boundary.

See `forge-recovery` for what happens when the orchestrator crashes mid-pipeline (P4 Stage A checkpoint replay).
