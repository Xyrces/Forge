# Operator cookbook

Common scenarios you'll hit while running the orchestrator. Each recipe is one focused thing you can copy-paste.

## Run the orchestrator

```bash
# 0. Pre-flight: confirm config + DB schemas + GitHub + kilo gateway auth
# (no dispatch; exits non-zero on any failure; useful for CI/smoke)
dotnet run --project PortHorizon.Agents -- --check

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

A spec with `status: Approved` (or `Designed`, or `Groomed` for re-decompose) is the input to the GroomerAgent. The groomer reads the spec, infers 1–3 stories, decomposes each into 1–3 tasks, and persists the decomposition as `type=story` and `type=task` issues linked to the spec via `parent_issue_id`.

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

## Design a spec

Specs in `ReadyForDesign` (or `Draft`, `NeedsRevision` for re-runs) are eligible for the Designer. The Designer runs:

1. A deterministic `DesignHygieneChecker` pass on 10 rules (missing acceptance criteria, broken dep chain, undefined module, duplicate epic, no touches, body too long, stale open questions, status mismatch, etc.). If the report has any Error findings, the run is marked `HygieneFailed` and the LLM is NOT called.
2. A MAF agent call against the kilo gateway. The agent has six AIFunctions (`db_get_spec`, `db_get_codebase_graph`, `db_get_existing_design_artifacts`, `db_get_visual_language`, `db_save_design_artifact`, `db_set_spec_status`). The system prompt is strict about always calling `db_set_spec_status` at the end.
3. The LLM writes 1-N `design_artifact` rows (wireframe / mockup / component-spec / visual-rule) and transitions the spec to one of `Designed` (visual spec done) / `Approved` (non-visual fast-path) / `NeedsRevision` (structural problem).

The Designer scheduler wakes up every 5 min and picks up `ReadyForDesign` specs. For manual runs:

```bash
# Trigger a manual design run on a spec
curl.exe -X POST http://127.0.0.1:4097/api/specs/<spec-id>/design

# Check the design timeline
curl.exe "http://127.0.0.1:4097/api/designer/runs?specId=<spec-id>"

# View the artifacts
curl.exe "http://127.0.0.1:4097/api/specs/<spec-id>/design-artifacts"
```

The dashboard's **Design** tab renders the spec list, the per-spec artifact body (HTML in sandboxed iframe / SVG inline / markdown in `<pre>`), and the persisted `HygieneReport` JSON with per-rule findings.

**Visual-language rules:** The Designer's first run produces a `kind=visual-rule` artifact. Future runs read it via `db_get_visual_language` and apply its color/typography/layout conventions. The system is self-bootstrapping: each project develops its own visual language on the first UI spec.

## Run the Artist on a Designed spec

After the Designer transitions a spec to `Designed`, the **Artist** runs and produces the actual art assets via the [Meshy](https://docs.meshy.ai/) REST API (text-to-3d, image-to-3d, multi-image-to-3d, rigging). Set the Meshy API key in `appsettings.json`:

```json
"llm": {
  "meshyApiKey": "msy_...",
  "meshyBaseUrl": "https://api.meshy.ai",
  "meshyPollIntervalSeconds": 5,
  "meshyMaxWaitSeconds": 600,
  "meshyMaxConcurrentJobs": 4
}
```

The Artist runs:

1. Reads the Designer's design_artifacts (wireframe HTML / visual-rule markdown) to ground its art submissions.
2. Calls `db_submit_meshy_job` per visual element. The mode depends on the input:
   - `text-to-3d` when the spec has a clear prompt but no reference image.
   - `image-to-3d` when a wireframe is rendered to a 2D image (public URL or data URI).
   - `rigging` when the spec needs a rigged model for animation (input is the `glb_url` of a prior text-to-3d / image-to-3d job).
3. The Meshy job is polled to completion inside the tool. On `SUCCEEDED` the `.glb` is downloaded to `.portHorizon/art-output/{spec}/{art-id}.glb`.
4. Calls `db_save_art_output` for each successful job, recording the asset path + the Meshy task id in `references_json`.
5. Transitions the spec to `AssetReady` (or `NeedsRevision` when the visual requirements are unclear).

The Artist scheduler wakes up every 5 min and picks up `Designed` specs. For manual runs:

```bash
# Trigger a manual art run on a Designed spec
curl.exe -X POST http://127.0.0.1:4097/api/specs/<spec-id>/design-art

# Check the art timeline
curl.exe "http://127.0.0.1:4097/api/artist/runs?specId=<spec-id>"

# View the produced assets
curl.exe "http://127.0.0.1:4097/api/specs/<spec-id>/art-output"

# Stream the .glb (or png / mp4) for an art output
curl.exe "http://127.0.0.1:4097/api/art-output/<art-id>/file" --output asset.glb
```

The dashboard's **Art** tab renders the spec list, the per-spec art output body (GLB via `<model-viewer>` / PNG via `<img>` / MP4 via `<video>`), the Meshy task list with status, and the persisted `artist_run` log.

The Groomer gate widens to `Designed | AssetReady | Approved | Groomed` — both visual specs (with Meshy art) and non-visual specs (operator-approved) flow into the Groomer.

**Meshy credits:** A text-to-3d preview task is 20 credits, a refine/texture task is 10 credits, image-to-3d is 20-30 credits. The Meshy 6 model is current (set via `ai_model: "meshy-6"` in the request).

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

## Read a dispatch log line and know which stage failed

Every successful dispatch produces a recognizable sequence of log lines, in order. Match what you see in the log to the stage table below.

### The happy-path log sequence

```
13:42:01.102 info: PortHorizon.Agents[0] Starting dashboard
13:42:01.731 info: PortHorizon.Agents.Dashboard.DashboardHost[0] Dashboard listening on http://127.0.0.1:4097
13:42:01.750 info: PortHorizon.Agents[0] Orchestrator starting
13:42:05.221 info: PortHorizon.Agents.Orchestrator.OrchestratorAgent[0] Issue task-1 transition Pending -> InProgress (type=task)
13:42:48.901 info: PortHorizon.Agents.Orchestrator.OrchestratorAgent[0] Agent session for task-1 completed in 43680ms
13:42:48.940 info: PortHorizon.Agents.Orchestrator.OrchestratorAgent[0] Opened PR #42 for task-1
13:42:48.965 info: PortHorizon.Agents.Orchestrator.OrchestratorAgent[0] Task task-1 dispatched to PR #42 (duration 43743ms)
```

If you see all 4 "task" lines, dispatch succeeded. The 30s PRWatcher poll picks it up after that.

### Stage table

| Stage | Log line prefix | What it does | What failure looks like |
|---|---|---|---|
| **Claim** | `Issue <id> transition Pending -> InProgress` | `IssueStore.ClaimAsync` atomically transitions the issue. | If you see `already-claimed` in the result and the issue didn't move, two orchestrators raced. |
| **Worktree** | (silent unless it errors) | `GitWorktreeService.CreateAsync` shells `git worktree add` to make `.portHorizon/worktrees/<id>/`. | `git worktree add` failed — usually a leftover from a previous crashed run. `git worktree prune` + delete the `.portHorizon/worktrees/<id>/` directory. |
| **RunAgent** | `Agent session for <id> completed in <ms>ms` | `MafAgentRunner.RunAsync` calls the kilo gateway and runs the MAF agent loop with bash. | (a) HTTP 401/403 from the gateway → API key expired. (b) bash command timed out. (c) Model produced no diff → next stage transitions to Completed without a PR. |
| **Commit** | (silent) | `GitWorktreeService.CommitAllAsync` shells `git add -A` + `git commit` on the worktree. | "no changes" warning — agent produced no diff. Issue moves to `Completed` with `"no changes (agent made 0 edits)"`. |
| **Push** | (silent) | `GitWorktreeService.PushAsync` shells `git push -u origin agent/<id>`. | Network/auth failure. The issue stays `InProgress` until the orchestrator's next dispatch cycle retries. |
| **PR open** | `Opened PR #<N> for <id>` | `GitHubService.CreatePullRequestAsync` calls `POST /repos/{owner}/{repo}/pulls`. | (a) GitHub token expired / wrong scope. (b) Branch already had a PR. (c) Rate limit. Issue gets `Failed` after retry budget. |
| **Watch enqueue** | (silent) | `IssueStore.CreateAsync` enqueues a `pr-watch` follow-up. | — |
| **PR watch** | `Watch <id> complete` (only logged on the ProcessWatchTaskAsync path) | `PRWatcher` polls GitHub PR status every 30s. | `Watch issue <id> crashed` — usually a transient GitHub API error. Re-pickup on next orchestrator start. |

### Common failure shapes and what to do

**`Issue <id> transition Failed: <error>`** — the agent's model call returned an error. The error string tells you why. Most common:

- `HTTP 401` or `HTTP 403` from the kilo gateway → API key expired. Rotate via [the kilo gateway docs](https://kilo.ai/docs/gateway). The orchestrator keeps running; new tasks will fail the same way until you restart with a fresh key.
- `HTTP 429` from the kilo gateway → rate limited. The orchestrator retries with backoff per the LLM config; check the `retryCount` metadata on the issue.
- The LLM is unreachable → network/DNS issue. Check `curl https://api.kilo.ai/api/gateway/models` from the host.

**`Agent session for <id> completed in <N>ms` followed by NO `Opened PR` line** — the agent produced no diff. Issue moves to `Completed` with reason `"no changes (agent made 0 edits)"`. The model's response is captured in `metadata.modelResponse`; inspect via the dashboard's Tasks tab.

**`git worktree add` failed inside a stack trace** — stale worktree from a previous crash. `git worktree prune` + `Remove-Item .portHorizon/worktrees/<id> -Recurse -Force`.

**`Opened PR` line present, but `pr-watch` issue not created** — bug, not operator-fixable. Check the orchestrator log for the exception.

**Watch issue stuck in `InProgress` for hours** — the PRWatcher is polling but GitHub isn't returning a verdict. Check the PR URL (in the dev task's metadata) — is the PR actually open? Is CI running? Was the PR closed or merged out-of-band?

## Skills + memory

The orchestrator bootstraps the operator-maintained `Xyrces/godot-ecs-gamedev-playbook` into the agent memory layer at startup. Every agent prompt sees:

- `playbook/repo` — the repo URL (default: `https://github.com/Xyrces/godot-ecs-gamedev-playbook`)
- `playbook/snapshot` — a one-line description of the playbook (39 skills across 8 categories)
- `playbook/skills/<role>` — a pipe-separated list of skill names relevant to that role (coredev, clientdev, qa, reviewer, intake, designer)

The bootstrap is **idempotent** — `MemoryStore.SeedIfMissingAsync` skips writes when the key already exists, so operator edits to any of these memory keys survive orchestrator restarts. To force a re-seed, delete the relevant key (the dashboard's Memory tab lets you do this) and the next start writes the default.

To update the playbook (e.g. the operator pushes new skills upstream), edit the memory keys directly. The agent's AIFunction + bash tool let it `curl <repo>/skills/<name>/SKILL.md` on demand, so the model decides which skills to actually read.

**Designer-specific layout:** the Designer's system prompt includes a `## Skills reference` block listing the per-role designer skills + the repo URL, with the instruction "If a skill is relevant to this spec, you may `curl <repo>/skills/<name>/SKILL.md` to read its full body before deciding. Don't fetch skills that aren't relevant." So the prompt isn't bloated with skill bodies — just the names.

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
