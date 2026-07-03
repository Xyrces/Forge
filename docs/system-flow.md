# PortHorizon.Agents — system flow

End-to-end view of what runs when a task is dispatched.

## Component map

```
                ┌────────────────────────────────────────────────────┐
                │              PortHorizon.Agents.exe               │
                │                                                    │
   operator ──► │  ┌────────────┐    ┌──────────────┐    ┌───────┐  │
   curl / SSE   │  │  Dashboard │◄──►│ Orchestrator │◄──►│PRWatch│  │
                │  │ (Kestrel)  │    │   Agent      │    │  er   │  │
                │  │  :4097     │    └──────┬───────┘    └───┬───┘  │
                │  └─────┬──────┘           │                │      │
                │        │                  │                │      │
                │  ┌─────▼──────┐    ┌───────▼────────┐  ┌────▼───┐  │
                │  │ /api/state │    │ MafAgentRunner │  │ GitHub │  │
                │  │ /api/...   │    │  (MAF + bash   │  │  API   │  │
                │  │            │    │   AIFunction)  │  └────┬───┘  │
                │  └────────────┘    └───────┬────────┘       │      │
                │                            │                │      │
                │  ┌────────────┐    ┌────────▼──────┐   ┌─────▼───┐  │
                │  │ JSONL      │    │ kilo gateway  │   │ PortHo-│  │
                │  │ Mirror     │    │ (HTTPS, OAI-  │   │ rizon  │  │
                │  │ (.jsonl)   │    │  compatible)   │   │  repo  │  │
                │  └────────────┘    └───────────────┘   └────────┘  │
                │                                                    │
│  ┌────────────────────────────────────────────┐  │
                 │  │       IssueStore (SQLite, schema v10)     │  │
                 │  │  issue  issue_dep  issue_event  memory   │  │
                 │  │  design_artifact  designer_run            │  │
                 │  │  art_output       artist_run             │  │
                 │  │  issue_groomer_run spec                  │  │
                 │  └────────────────────────────────────────────┘  │
                 │                                                    │
                 │  ┌────────────────────────────────────────────┐  │
                 │  │  Meshy REST API (text-to-3d, image-to-3d, │  │
                 │  │  multi-image-to-3d, rigging)               │  │
                 │  └────────────────────────────────────────────┘  │
                 └────────────────────────────────────────────────────┘
```

## Data flow: dispatch one task

```
operator / dashboard / CLI
  │
  │  POST /api/state/issues   {type, title, description, ...}
  ▼
IssueStore.CreateAsync           ──►  issue row (status=Pending, type=task)
  │
  │  every 3s: DispatchSingleTaskAsync on ReadyAsync()
  ▼
IssueStore.ClaimAsync            ──►  status=InProgress, assignee=kilo
  │                                  metadata: branch=agent/<id>
  ▼
GitWorktreeService.CreateAsync   ──►  branch agent/<id>, worktree at
  │                                   .portHorizon/worktrees/<id>
  ▼
MafAgentRunner.RunAsync           ──►  MAF agent loop with:
  │                                   role instructions from .kilo/agents/<role>.md
  │                                   memory block (## Project memory) from MemoryStore
  │                                   prompt with task body + worktree path
  │                                   bash AIFunction → cmd.exe /c <command>
  │
  │  result.Text captured
  ▼
IssueStore.UpdateMetadata        ──►  metadata.modelResponse = result.Text
  │                                  metadata.agentSessionId
  ▼
GitWorktreeService.CommitAllAsync ──►  commit "Task(<id>): <title>" on agent/<id>
  │
  │  if no changes → TransitionAsync(Completed, "no changes (agent made 0 edits)"), return
  ▼
GitWorktreeService.PushAsync     ──►  push agent/<id> to origin
  ▼
GitHubService.CreatePullRequestAsync
  │                              ──►  PR opened (head=agent/<id>, base=main)
  ▼
IssueStore.UpdateMetadata        ──►  metadata.prNumber, metadata.branchSha
  │                                  IssueStore.TransitionAsync(Completed)
  ▼
IssueStore.CreateAsync           ──►  pr-watch follow-up issue (type=pr-watch)
  │
  │  next dispatch cycle: PRWatcher picks it up
  ▼
PRWatcher.ProcessWatchTaskAsync  ──►  poll PR status every 30s
  │   on green CI + approval:
  │     Octokit: merge PR, delete branch
  │     GitWorktreeService.RemoveAsync
  │     IssueStore.TransitionAsync(Completed)
  │   on REQUEST_CHANGES:
  │     IssueStore.TransitionAsync(Blocked)
  │   on red CI:
  │     IssueStore.TransitionAsync(Failed)
  ▼
IssueStore row updated           ──►  JSONL mirror reflects on next 5s tick
                                     Dashboard SSE fires the transition event
```

## Where state lives

| Concern | Storage | Access pattern |
|---|---|---|
| task queue + dep graph | `issues.db` (SQLite, IssueStore) | WAL, concurrent readers, single-writer orchestrator |
| persistent memory | `memory.db` (SQLite, MemoryStore) | prime-injected into every agent prompt |
| visual design | `design_artifact` + `designer_run` (SQLite, schema v9) | per-spec artifacts, timeline of runs |
| produced art | `art_output` + `artist_run` (SQLite, schema v10) | per-spec `.glb`/`.png`/`.mp4` paths, Meshy task list |
| spec state machine | `spec` + `spec_status` (SQLite) | Draft → ReadyForDesign → Designed → AssetReady → ReadyForGroom → Grooming → Groomed → Shipped |
| event log | `issues.db.issue_event` + `IssueStore.AddEventAsync` | appended per transition, queryable for the dashboard |
| heartbeat + counters | `orchestrator-state.json` (StateStore) | small JSON, view-only for the dashboard |
| tail-able mirror | `issues.jsonl` (IssuesJsonlMirror) | rewritten every 5s, sorted by id, atomic rename |
| git worktrees | `.portHorizon/worktrees/<id>/` | one per task; cleaned up by `GitWorktreeService.RemoveAsync` on merge |
| branches | PortHorizon git repo | one `agent/<id>` per task; deleted on merge |
| produced art files | `.portHorizon/art-output/{spec}/{art-id}.{ext}` | relative path stored in `art_output.body`; served at `/api/art-output/{id}/file` |
| PRs | github.com/Xyrces/PortHorizon | `PRWatcher` polls; merged or closed per review |

## Why a JSONL mirror when the DB is the source of truth?

The DB is the source of truth. The JSONL is a viewer artifact — a file the operator can `tail -f` from outside the orchestrator host, or `git diff` to review the project's history of tasks. It is regenerated every 5s by the `IssuesJsonlMirror` background service; if the file ever disagrees with the DB, the DB wins. This is exactly the role `bd issues.jsonl` plays in the `gastownhall/beads` design we modeled `docs/embedded-issues.md` after — adapted to in-process SQLite instead of a separate process.

## Pipeline: Intake → Product → Designer → Artist → Groomer → Engineering

The full pipeline for turning a user prompt into a merged PR is six stages. Each stage is an orchestrator-owned background service or agent; the orchestrator's `DispatchCycleAsync` picks up Ready issues, calls the next stage in line, and the next stage's scheduler picks up the result on its next tick.

```
   user prompt                  product refines            design artifacts
       │                              │                            │
       ▼                              ▼                            ▼
   IntakeAgent ──► Spec(Draft) ──► ProductAgent ──► Spec(ReadyForDesign) ──► DesignerAgent ──► Spec(Designed)
                                                                              │                        │
                                                                              ▼                        ▼
                                                                          design_artifact          ArtistAgent ──► Spec(AssetReady)
                                                                          rows written                   │              │
                                                                                                        ▼              ▼
                                                                                                   Meshy REST API   art_output rows
                                                                                                   (text-to-3d,    written
                                                                                                    image-to-3d,
                                                                                                    rigging) ──► .glb downloaded
                                                                                                                       │
       ┌────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
       ▼
   GroomerAgent ──► Spec(Groomed) ──► Spec(Shipped on merge)
       │
       ▼
   EngineeringDispatchWorkflow (Claim → Worktree → RunAgent → CommitPushPr → EnqueueWatch) ──► PR opened ──► PRWatcher ──► merged
```

Each `Spec` row carries its current status; the next stage's scheduler filters by status to find candidates. Manual triggers (`POST /api/specs/{id}/design`, `POST /api/specs/{id}/design-art`, `POST /api/specs/{id}/groom`) bypass the scheduler and run the agent immediately in a background task.

## Dependency graph semantics

Edges in `issue_dep` have a `kind`:

| Kind | Behavior |
|---|---|
| `blocks` | `IssueStore.ReadyAsync` excludes the issue if the blocker is not `Completed`/`Closed`. `Failed` is intentionally open (operator must close or remove). |
| `related` | informational; shown in the dashboard graph, not enforced |
| `duplicates` | informational |

Self-loops are rejected. Cycle detection is by operator inspection — the orchestrator is single-writer so cycles are rare and self-resolving.

## Memory recall

Every agent run calls `MemoryStore.RecallAsync()` before constructing the system prompt. The returned memories are rendered as:

```markdown
## Project memory

Persistent insights from past work. Apply where relevant; do not quote verbatim unless the task asks.

- **ports/commit-style** _(expires 2027-07-02)_:
  When writing commit messages in this repo, prefix with POR-XXXX-NNNN: ...
```

Memories are stored in `memory.db` with an optional `ttl_days`. Expired rows are filtered out of `RecallAsync` (not deleted; sweep via `MemoryStore.PurgeExpiredAsync`).

Add a memory via `POST /api/memory {key, body, ttlDays?}` or `bash`:

```bash
curl.exe -X POST http://127.0.0.1:4097/api/memory \
  -H "Content-Type: application/json" \
  -d '{"key":"coding-style/no-alloc","body":"Pre-allocate buffers in hot paths; avoid new[] in Update()."}'
```

The next dispatch picks it up.

## MAF Workflows — parallel implementation

`Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` builds the same five stages as a `WorkflowBuilder` graph:

```
[Claim]  →  [Worktree]  →  [RunAgent]  →  [CommitPushPr]  →  [EnqueueWatch]
```

Each stage is a `FunctionExecutor<TIn, TOut>` with a `public static HandleAsync` for direct testing. AlreadyClaimed / NoDiff / Skipped are first-class result variants the typed channels route; no conditional edges.

The orchestrator's `DispatchSingleTaskAsync` still uses sequential code (it's the production path). The workflow version is exercised by `EngineeringDispatchWorkflowTests` and is the planned replacement once behavioral parity is fully verified.

For the dependency-graph and JSONL mirror design that `docs/embedded-issues.md` describes and the codebase has now implemented, see [embedded-issues.md](embedded-issues.md).
