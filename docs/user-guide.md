# Forge user guide

For the operator who uses Forge day-to-day: feeding it work, steering sprints, watching the review loop, and unblocking the pipeline. For installation and service administration see [`administrator-guide.md`](administrator-guide.md); for copy-paste recipes see [`operator-cookbook.md`](operator-cookbook.md).

## 1. The mental model

Work flows through two lanes:

```
Planning lane:   intake → spec → design → groom → backlog (stories/tasks)
Implementation:  backlog → sprint → dispatch (agent run) → PR → review/CI → merge
```

Four things to internalize:

1. **All engineering work happens inside a sprint.** There is no "just run this task" button — the SprintAssembler completes the Active sprint when its tasks are terminal and assembles + activates the next one from groomed, eligible work. Sprints are themed, coherent, independently deployable units: assembly packs all eligible work sharing a theme (a groomed spec, or a follow-up chain's root task) into one sprint, choosing the highest-priority theme first. Only truly unrelated operator-enqueued tasks each get their own solo sprint.
2. **Nothing enters a sprint ungroomed.** Ad-hoc tasks (operator-enqueued, agent-filed follow-ups) sit in the backlog until the ScheduledGroomer marks them `groomed=true` (or closes them with a reason).
3. **The task IS the watch.** Once a PR exists, the task row carries `prNumber`/`branch` metadata and the 15-minute sweep drives review, rework, and merge. No manual PR babysitting.
4. **The operator steers, the system executes.** Your levers: intake, requeues, stage gates, role model overrides, slot caps, and the editable workflow — not hand-merges or hand-pushes.

## 2. Getting work in

### New work (epics) — Intake

Open `/intake` and chat with the Intake agent. Describe the feature; the agent proposes an epic (title, description, stories). Accept it and the epic enters the planning lane: design → grooming → stories/tasks → backlog.

### Ad-hoc tasks — three ways

- **Dashboard:** Tasks page → enqueue form.
- **CLI:** `dotnet run --project Forge -- --enqueue-task "Title" --task-type ecs --task-desc "..." --branch "agent/thing"`.
- **API:** `POST /api/state/issues`.

Ad-hoc tasks land in the backlog as ungroomed. The groomer pass verifies them against the project vision and current repo state, sets their priority relative to the rest of the open work, then marks them groomed or closes them. Groomed ad-hoc work is either injected into the Active sprint (only when it genuinely belongs there: it blocks a sprint member, is operator-P1, or was requeued by you) or assembled into a themed sprint — follow-ups pack with the rest of their chain's work, so related fixes ship together.

### Agent-filed follow-ups

Dev/QA/Reviewer runs can file follow-ups via their `file_followup` tool. These are deliberately parentless — they re-enter through grooming like everything else. `followUpOf` metadata is the audit trail. Follow-ups are tracked during a sprint but only created once the sprint completes.

## 3. Steering the pipeline

### Stage gates

`/ops/gates` (or `GET/POST /api/gates*`) holds/releases the four automatic transitions:

| Gate | What holding it does |
|---|---|
| `design` | Designer scheduler skips its tick |
| `groom` | Groomer scheduler skips its tick |
| `sprint` | Sprint assembly pauses (completing a finished sprint still happens — that's bookkeeping) |
| `merge` | PR watches stay live but nothing merges; external merges are still detected |

Use holds when you need to review a batch before it flows onward (e.g. hold `sprint` to groom follow-ups before the next sprint starts).

### The Flow page

`/flow` is the live pipeline DAG: planning lane vs implementation lane, per-node counts. `/flow?issue={id}` shows one issue's full journey from the event timeline — **the first stop for "where is my work stuck?"**.

`/flow?mode=edit` is the workflow control surface: edit the pipeline definition as a draft, validate, publish. Wiring and policy changes (auto-merge on/off, rework limits, step toggles, gate attachment) apply without restart. The state transition table itself is code-owned.

### Models and prompts

`/agents` is the agent control surface. Per role: identity, territory, tools, the full role prompt (and whether it comes from a project override or the built-in), the effective provider+model, live slot usage, and the current/last run with heartbeat.

- **Change a role's model live:** `PUT /api/agents/roles/{name}/model` (or the UI). Overrides are project-scoped, DB-backed, and apply to the next run — no restart. `DELETE` removes the override. Resolution: override → `llm.roles` config → provider default.
- **Role prompts** come from `<projectRoot>/agents/<role>.md` when the project ships one, else the built-in copy. Edit the file in the project repo to change behavior.

### Slot caps

Per-project role concurrency lives on the project drill-down (`/projects/{id}/overview`) — `PUT /api/projects/{id}/roles`. Defaults: coredev/clientdev/reviewer=2, everything else=1.

## 4. The review loop

Once a dev run pushes a branch, the orchestrator opens the PR and the 15-minute watch sweep takes over:

1. **Reviewer agent review** — every watched PR gets one; verdict recorded on the task (`reviewVerdict`/`reviewSha`/`reviewRound`) with a GitHub comment as the audit.
2. **Merge gate** — merge requires green check runs AND an approval at the current head SHA (formal review or reviewer-agent verdict).
3. **Rework rounds** — red CI or changes-requested requeues the task on the same branch/PR with the failure context in the prompt. The task shows Pending + a PR number and an R1/R2/R3 pill in the UI.
4. **Circuit breaker** — 3 strikes (CI failures + changes-requested share one counter) and the task goes Blocked/Failed with a breaker snapshot. It's yours at that point: inspect the failure, then close the task or requeue it (`POST /api/tasks/{id}/requeue`).
5. **Merge** — green + approved → merge, branch deleted, worktree removed, task Completed. Externally-merged PRs are detected and handled.

Non-blocking reviewer findings are NOT requested-changes — the reviewer approves and files them as follow-ups, which re-enter through grooming.

Special states you'll see:

- **Parked (infra)** — the base branch's own CI was failing; the task parks without a strike and gets one no-strike refresh round when base CI recovers.
- **Blocked (reviewer-unavailable)** — auto-resumes up to 3 times before needing you.
- **Conflict sync** — a merge conflict triggers a dedicated sync rework round; only one sync runs per store at a time.

## 5. Projects, secrets, skills

- **Projects** (`/projects`): register repos, sync default branches, tune role caps. Everything except `/now` is per-project-scoped — `/now` + the alert inbox is your unified cross-project admin view.
- **Secrets** (`/projects/{id}/secrets`): per-project encrypted credentials (git tokens, provider keys, custom kinds). Agents consume them by reference as env vars; values never appear in prompts, transcripts, or logs.
- **Skills** (`/skills`): knowledge injected into agent prompts. Global skills apply to every project; project-scoped skills only to that project's runs. Skills with `source=repo` are imported from the project's `.kilo/skills/<name>/SKILL.md` at startup — **edit the file, not the dashboard** (the dashboard returns 409 on repo-owned edits). UI-owned skills (`source=forge`) are edited in place.

## 6. Memory

`/ops/memory` (or `GET/POST /api/memory`) manages the persistent project memory injected into every agent prompt under "## Project memory". Use it for durable facts: coding conventions, architecture decisions, incident learnings. Sprint-scoped shared context lives under `sprint/{id}/` keys; the project vision under `vision/<projectId>`.

## 7. Operator rules of engagement

1. **No manual out-of-loop fixes.** Don't hand-merge, hand-push, or hand-patch around a stuck pipeline — fix the system or surface it. Manual steps leave dangling state the loop can't see.
2. **Don't auto-clear Failed blockers.** A `Failed` issue blocking others is intentionally open. Close it explicitly or remove the `blocks` edge.
3. **Everything through the UI/API.** Task state goes through `IssueStore` (dashboard, API, CLI) — never direct DB writes.
4. **The dashboard updates in place.** Pages use Fluxor state; if you find yourself hard-refreshing to see changes, that's a bug worth filing.
5. **Use the gates, not the kill switch.** Prefer holding a stage over stopping the service when you want the pipeline to pause.

## 8. Quick reference

| You want to… | Go to |
|---|---|
| See what's happening right now | `/now` |
| Trace one piece of work | `/flow?issue={id}` |
| Add new feature work | `/intake` |
| Pause a pipeline stage | `/ops/gates` |
| Change which model a role uses | `/agents` |
| Inspect a stuck/failed run | `/runs` → run detail; `<dataRoot>/logs/agent.log` |
| Unblock a failed task | `/tasks` → task → requeue or close |
| See cost/token usage | `/ops/cost` |
| Inspect recovery audit | `/ops/recovery` |
| Register a repo / rotate a token | `/projects`, `/projects/{id}/secrets` |
| Pre-flight the service | `dotnet run --project Forge -- --check` |
