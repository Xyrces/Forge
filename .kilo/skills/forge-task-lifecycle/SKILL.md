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

Production is the sequential code in `OrchestratorAgent.DispatchSingleTaskAsync` (`Orchestrator/OrchestratorAgent.cs:123`). The `Orchestrator/Workflow/` directory builds the same five stages as a MAF WorkflowBuilder graph (typed `FunctionExecutor<TIn, TOut>` instances), which is exercised by `EngineeringDispatchWorkflowTests` but **not** the production dispatcher. Both go through `IWorkflowDispatcher`; today the in-process implementation (`Orchestrator/DurableDispatcher.cs`) wins; with `Orchestrator:Execution=Durable`, the DTS-backed implementation wins (P4 Stage B — see `forge-recovery`).

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
  - `BashTool(workingDirectory=<worktree>)` AIFunction — `cmd.exe /c <command>` on Windows, `bash -c <command>` elsewhere; default `workingDirectory` is the task's worktree. Resolved via `context["worktreePath"]`.
  - `ArtifactReadTool` — when stores are wired; lets agents pull a single artifact body on demand rather than have the orchestrator inline every body.
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

## Stage 5 — EnqueueWatch

`OrchestratorAgent.EnqueueWatchIssueAsync` (called by the commit/push/PR executor or by the sequential path):

- Creates a new issue of type `pr-watch` (`AgentTaskTypes.PrWatch = "pr-watch"`).
- Metadata: `prNumber`, `branch`, `worktreePath`, `taskId=<devIssueId>`.
- Title: `Watch PR #<n> for <devIssueId>`.
- The orchestrator's next dispatch cycle sees it in `ReadyAsync` and hands it to `PRWatcher.ProcessWatchTaskAsync`.

## PRWatcher — what watches the watch

`Orchestrator/PRWatcher.cs`:

- Polls GitHub every 30s (`Spawner.PollIntervalSeconds` is 3s for dispatch, but PRWatcher is a separate cadence).
- **Green CI + approval:** Octokit merges the PR, deletes the branch, removes the worktree via `GitWorktreeService.RemoveAsync`, transitions the dev task to `Completed`.
- **`REQUEST_CHANGES`:** transitions to `Blocked`.
- **Red CI:** transitions to `Failed`.

## Retry / failure semantics

`OrchestratorAgent.HandleFailureAsync`:

- `_maxRetryCount = 1`. If the workflow dispatcher threw, transition to `Pending` with `retryCount+1` and log a warning — the next dispatch cycle picks it up.
- After the retry: hard-fail to `Failed`. If a worktree path exists, `GitWorktreeService.RemoveAsync` is called best-effort.
- `OperationCanceledException` → `Failed("cancelled")` and exit.

## The two implementation paths

| Aspect | Sequential (production) | MAF Workflows (dormant) |
|---|---|---|
| Source | `OrchestratorAgent.DispatchSingleTaskAsync` | `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` + `*Executor.cs` |
| Stage shape | Inlined code | Typed `FunctionExecutor<TIn, TOut>` instances |
| Short-circuits | `if (claimed is null)` early return; `if no diff` early return | `AlreadyClaimed` / `NoDiff` / `Skipped` first-class result variants; typed channels route them |
| Tested by | `OrchestratorAgentTests`, `EngineeringDispatchWorkflowTests` | `EngineeringDispatchWorkflowTests` against a real temp git repo |
| Status | Live | Behavioral parity not yet fully verified → not swapped in |

The orchestration skill is correct regardless of which path is wired, because the externally observable state transitions are the same.

## Why this matters

This is the only flow where multiple subsystems meet: `IssueStore` (claim), git (worktree + branch), the LLM (MAF agent), GitHub (PR), and `IssueStore` again (pr-watch follow-up). When reasoning about "why is this task in state X" or "why didn't the agent's changes ship", walk through these stages in order — most bugs are at a stage boundary.

See `forge-recovery` for what happens when the orchestrator crashes mid-pipeline (P4 Stage A checkpoint replay).
