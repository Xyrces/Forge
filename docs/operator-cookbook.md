# Operator cookbook

Common scenarios you'll hit while running the orchestrator. Each recipe is one focused thing you can copy-paste.

## Run the orchestrator

```bash
# 1. Make sure appsettings.json is filled in
cp appsettings.example.json appsettings.json
# edit appsettings.json: set llm.providers[0].apiKey + github.token

# 2. Run
dotnet run --project PortHorizon.Agents

# 3. In another terminal, watch the dashboard
start http://127.0.0.1:4097
```

Stop with `Ctrl+C`. The orchestrator cancels the dispatch loop, waits for in-flight agent runs to complete, then shuts down cleanly.

## Queue a task

Three ways, pick whichever fits your workflow:

```bash
# A) CLI
dotnet run --project PortHorizon.Agents -- \
  --enqueue-task "Add Position ECS component" \
  --task-type ecs \
  --task-desc "Create a Position struct with x,y floats; add to AtmosphereSystem." \
  --branch "agent/position"

# B) HTTP (from the dashboard's Tasks tab, or curl)
curl.exe -X POST http://127.0.0.1:4097/api/state/issues \
  -H "Content-Type: application/json" \
  -d '{
    "type":"task",
    "title":"Add Position ECS component",
    "description":"Create a Position struct with x,y floats; add to AtmosphereSystem.",
    "priority":2
  }'

# C) Direct SQL (only when nothing else works)
# Don't do this unless you know what you're doing — the schema is v7
# with FK constraints. Use the HTTP path.
```

Watch it land on the dashboard; the orchestrator picks it up within `spawner.pollIntervalSeconds` (default 3s).

## Use the right role for the task

The `type` you pass to `--enqueue-task` or `POST /api/state/issues` maps to a role via `RoleAgentRegistry.FromTaskType`:

| Type string | Role |
|---|---|
| `ecs`, `systems`, `atmospherics`, `pathfinding`, `mcp` | `CoreDev` (PortHorizon.Core/) |
| `client`, `ui`, `godot`, `syncbridge` | `ClientDev` (PortHorizon.Client/) |
| `test`, `playtest`, `qa` | `QA` (read-only, builds + tests) |
| `review` | `Reviewer` (architecture review on GitHub) |

Anything else falls back to `CoreDev`. The role determines the system prompt, the project subdirectory, and the allowed tool set.

## Inject persistent knowledge

Project memory flows into every agent prompt as a `## Project memory` block. Add entries when you learn something the agent should remember next time:

```bash
# Add a memory
curl.exe -X POST http://127.0.0.1:4097/api/memory \
  -H "Content-Type: application/json" \
  -d '{
    "key":"coding-style/no-linq-in-hot-paths",
    "body":"Avoid LINQ in inner loops. PortHorizon ECS inner loops must be alloc-free."
  }'

# Add a memory with TTL (auto-decays)
curl.exe -X POST http://127.0.0.1:4097/api/memory \
  -H "Content-Type: application/json" \
  -d '{
    "key":"release/v1.0-pinned-deps",
    "body":"Until 2026-12-31: do not bump dotnet/Godot majors. Bump minors only.",
    "ttlDays":90
  }'

# List all memory (or filter by prefix)
curl.exe http://127.0.0.1:4097/api/memory
curl.exe "http://127.0.0.1:4097/api/memory?prefix=coding-style/"

# Delete a memory
curl.exe -X DELETE http://127.0.0.1:4097/api/memory/coding-style/no-linq-in-hot-paths
```

Namespacing convention: `<area>/<short-name>` — `coding-style/...`, `ports/...`, `release/...`, `infra/...`. Prefix queries make it cheap to scope.

## Decompose a spec into engineering tasks

A spec with `status: Approved` is the input to the GroomerAgent. The groomer reads the spec, infers 1–3 stories, decomposes each into 1–3 tasks, and persists the decomposition as `type=story` and `type=task` issues linked to the spec via `parent_issue_id`.

```bash
# Approve a spec (via the Spec tab in the dashboard, or curl)
# Then trigger the groomer
curl.exe -X POST http://127.0.0.1:4097/api/specs/<spec-id>/groom

# The groomer runs in the background. Refresh the Tasks tab to see
# the new story+task rows appearing.
```

The groomer agent itself uses MAF; it's also fire-and-forget. If the spec is too vague, the groomer may produce a thin decomposition. Approve a more specific spec or add memory entries first.

## Add a dependency between tasks

```bash
# task-2 is blocked by task-1
curl.exe -X POST http://127.0.0.1:4097/api/state/issues/task-2/deps \
  -H "Content-Type: application/json" \
  -d '{"blockerId":"task-1","kind":"blocks"}'

# Inspect task-2's deps
curl.exe http://127.0.0.1:4097/api/state/issues/task-2/deps
# -> { "issueId": "task-2", "blocked": true, "edges": [...] }

# Remove the edge
curl.exe -X DELETE http://127.0.0.1:4097/api/state/issues/task-2/deps/task-1/blocks
```

The dispatch loop's `ReadyAsync` query excludes blocked issues, so the dependent task won't run until the blocker is `Completed` (or `Closed`). `Failed` is intentionally treated as open — the operator must close the blocker or remove the edge.

## Debug a stuck task

A task can get stuck in `InProgress` if:
- the agent run is taking forever
- the agent threw an exception that wasn't retried
- the orchestrator crashed mid-dispatch (state is now stale)

```bash
# 1. Inspect the task
curl.exe http://127.0.0.1:4097/api/state/issues/<id>
# Look at: status, lastError, modelResponse, agentSessionId

# 2. Inspect the events (last few state transitions)
# No direct endpoint; query the issue via the dashboard's Tasks tab
# and click the row to see its event history.

# 3. Force a transition
curl.exe -X PATCH http://127.0.0.1:4097/api/state/issues/<id> \
  -H "Content-Type: application/json" \
  -d '{"status":"Failed","error":"manual: stuck in InProgress"}'

# Or back to Pending for another retry
curl.exe -X PATCH http://127.0.0.1:4097/api/state/issues/<id> \
  -H "Content-Type: application/json" \
  -d '{"status":"Pending"}'

# 4. Clean up the worktree if the orchestrator crashed
# (the dashboard will show whether the worktree still exists)
```

Stale `InProgress` tasks are auto-reaped at startup if `UpdatedAt` is older than `spawner.staleMinutes` (default 30m).

## Watch a PR through the review loop

When a task is dispatched and a PR is opened, the orchestrator auto-enqueues a `pr-watch` follow-up. The `PRWatcher` polls every 30s. Watch it on the dashboard:

```bash
# Find the watch issue
curl.exe "http://127.0.0.1:4097/api/state/issues?type=pr-watch"
```

The watch is `Pending` while waiting on CI, `InProgress` while reviewing, and transitions to `Completed` (merged) / `Blocked` (changes requested) / `Failed` (red CI) on verdict.

If you want to review a PR before the reviewer bot:

1. Open the PR on GitHub (URL is in the dev task's metadata: `metadata.prNumber`).
2. Approve / request changes / comment as usual.
3. The orchestrator picks up your review on its next 30s poll and proceeds.

## Tail the JSONL mirror

`IssuesJsonlMirror` rewrites `.portHorizon/state/issues.jsonl` every 5s. The file is sorted by `id`, one JSON object per line, atomic via temp + rename. Use it to:

```bash
# Watch live from outside the orchestrator host
tail -f .portHorizon/state/issues.jsonl | jq

# Review the project's task history
git add .portHorizon/state/issues.jsonl
git commit -m "Track: 2026-07-02 sprint task history"

# Grep across the history
grep -E '"status":"(Completed|Failed)"' .portHorizon/state/issues.jsonl | wc -l
```

The DB is the source of truth. The JSONL is a viewer artifact — if the two ever disagree, the DB wins.

## Stop the orchestrator cleanly

`Ctrl+C` in the terminal where the orchestrator is running. It will:

1. Stop the dispatch loop (no new claims).
2. Wait for in-flight agent runs to complete (or hit their timeout).
3. Flush the JSONL mirror one last time.
4. Exit.

Force-kill (`Stop-Process`) loses pending work and may leave a worktree behind. If that happens, clean up manually:

```bash
git -C <workspace> worktree list
git -C <workspace> worktree remove --force .portHorizon/worktrees/<id>
git -C <workspace> branch -D agent/<id>
```

## Rotate the kilo gateway key

The JWT in `appsettings.json` has an expiration (the `exp` claim in the JWT). When it expires:

1. Generate a new key at <https://kilo.ai>.
2. Update `appsettings.json` (or set `KILO_GATEWAY_API_KEY` env var).
3. Restart the orchestrator. In-flight agent runs will fail; pending tasks will retry on the next dispatch cycle.

If you set the env var, no file edit is needed — the orchestrator picks it up on restart.

## Add a new role

If you need a new role beyond `CoreDev` / `ClientDev` / `QA` / `Reviewer` / `Intake`:

1. Add the enum value to `IAgent.AgentType` in `IAgent.cs`.
2. Register the role in `Agents/RoleAgentRegistry.cs` (kilo agent name, project subdir, allowed tools).
3. Add a `LLM_ConfigRole` mapping in `appsettings.json` (`llm.roles.<NewRole>`).
4. Drop a system-prompt template at `<workspace>/.kilo/agents/<newrole>.md`.
5. Update `RoleAgentRegistry.FromTaskType` to map any task types you want to use the new role.

The orchestrator's startup fails fast on unknown role / unknown `llm.roles.<X>` keys, so the wiring is explicit.

## Reset the system

Sometimes you just want a clean slate. Delete `.portHorizon/` from the workspace root and re-run. The DB and JSONL are both recreated. Git worktrees in the workspace repo will be orphaned (the workspace's own worktree list shows them); `git worktree prune` cleans that up.

```bash
# Stop the orchestrator first
# (Ctrl+C in the terminal where it's running)

# Remove state
rm -rf .portHorizon/state
git worktree prune

# Restart
dotnet run --project PortHorizon.Agents
```

The workspace repo's branches (`agent/*`) and merged PRs are not affected — only the orchestrator's local state. Use `git branch -D agent/<id>` per-branch if you want to clean those up too.
