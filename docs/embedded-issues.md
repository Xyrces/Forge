# Embedded issue tracker — design

Status: **design draft** (no code yet). Goal: replace `StateStore`'s JSON-file task list with a proper in-process issue tracker that gives the orchestrator the capabilities `gastownhall/beads` gives an external agent — without paying the operational cost of Dolt as a separate process.

## What we're keeping from `beads` (the why)

| Beads feature | Why we want it |
|---|---|
| Structured issue records (status, type, priority, assignee, labels) | Replaces our ad-hoc `AgentTask.Status` enum + `Dictionary<string, object> Parameters` bag. Lets the dashboard filter/sort on real fields, not string keys. |
| Dependency graph (`blocks`, `related`, `parent-child`) | Lets us sequence work. Today we order by `CreatedAt`. A real DAG means "this PR review can't start until that CI fix lands." |
| Claim semantics (`bd update <id> --claim`) | Atomic single-writer assignment. Today's `OrchestratorAgent` walks all `Pending` tasks and dispatches in parallel — two dispatchers on the same state file would double-dispatch. Claim semantics make multi-orchestrator safe. |
| Hash-based IDs (`bd-a1b2`) | Today we use caller-supplied string IDs (`--enqueue-task my-task-001`). Hash IDs survive merges, don't collide on copy-paste, encode ordering. |
| `ready` query (no open blockers) | The dispatch loop's main job becomes "find a ready task, claim it, dispatch." |
| `issues.jsonl` export | Human-readable, line-oriented, git-diff-friendly snapshot of the queue. We can `tail` it, `grep` it, commit it for a "what was the queue at this point in time" record. Beads uses this exact mechanism for the same reason. |
| Memory / context (`bd remember`) | Project-level memory that gets injected into prompts. Today we have `.kilo/agents/*.md` for role memory but nowhere to store cross-task learnings ("Xyrc decided they want NetArchTest gates deferred"). |

## What we're explicitly NOT keeping

| Beads feature | Why we drop it |
|---|---|
| Dolt as the storage engine | External process, separate binary, hard to embed cleanly in .NET, not single-file. Replaces a `JsonSerializer`+atomic-rename with a separate moving part. The "Git for SQL" affordances (cell-level merge, branchable state) solve a problem we don't have: **one orchestrator writes to one state at a time.** |
| Multi-host sync (`bd dolt push/pull`) | We don't have multiple orchestrators writing the same state in production. If we ever do, SQLite WAL is a fine primitive to build on top of; Dolt is overkill. |
| Hierarchical IDs (`bd-a3f8.1.1`) | Add it if a real need appears. Don't add it speculatively — string parsing for the dashboard and the API gets uglier fast. |
| The `bd` CLI subprocess | Everything happens in-process; the dashboard IS the UI. No shell-out. |

## Storage

- **Engine:** Microsoft.Data.Sqlite (`sqlite-net` style embedded, no server). Single file at `.portHorizon/state/tracker.db`. WAL mode for concurrent reads + one writer.
- **Migrations:** Schema version row (`PRAGMA user_version`). Migrations are forward-only `.sql` files in `Migrations/` with a `__EFMigrationsHistory`-style applied table.
- **Hot path:** Single-writer orchestrator takes a `SemaphoreSlim(1,1)` on writes (already the pattern). WAL allows the dashboard to read concurrently.
- **Cross-process safety:** With WAL and a busy-timeout pragma (`PRAGMA busy_timeout = 5000`), two orchestrators don't corrupt the DB. The first writer wins; the second waits up to 5s. Single orchestrator is still the supported config; multi-orchestrator is a future concern.

## Schema (v1)

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE issue (
  id              TEXT PRIMARY KEY,                 -- hash id, e.g. "ph-7f3c1e2d"
  short_id        TEXT NOT NULL UNIQUE,             -- sequence within epoch, e.g. "ph-1"
  type            TEXT NOT NULL,                    -- 'task' | 'pr-watch' | 'epic' | 'memory'
  title           TEXT NOT NULL,
  description     TEXT,
  status          TEXT NOT NULL,                    -- 'open' | 'in_progress' | 'completed' | 'failed' | 'blocked' | 'closed'
  priority        INTEGER NOT NULL DEFAULT 2,       -- 0..3, like bd
  assignee        TEXT,                             -- role agent name
  created_at      TEXT NOT NULL,
  updated_at      TEXT NOT NULL,
  closed_at       TEXT,
  metadata_json   TEXT NOT NULL DEFAULT '{}'        -- escape hatch; structured fields preferred
);

CREATE INDEX ix_issue_status      ON issue(status);
CREATE INDEX ix_issue_assignee    ON issue(assignee);
CREATE INDEX ix_issue_updated_at  ON issue(updated_at);

-- Dependency graph: edges, not columns, because one issue can block many and be blocked by many.
CREATE TABLE issue_dep (
  blocker_id   TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
  blocked_id   TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
  kind         TEXT NOT NULL DEFAULT 'blocks',     -- 'blocks' | 'related' | 'parent' | 'duplicates'
  PRIMARY KEY (blocker_id, blocked_id, kind)
);
CREATE INDEX ix_issue_dep_blocked ON issue_dep(blocked_id);

-- Append-only audit trail; the "memory decay" comes from here.
CREATE TABLE issue_event (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  issue_id    TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
  ts          TEXT NOT NULL,
  actor       TEXT,                                -- orchestrator name, or 'dashboard', or 'cli'
  kind        TEXT NOT NULL,                       -- 'created' | 'claimed' | 'status_change' | 'note' | 'closed'
  detail_json TEXT
);
CREATE INDEX ix_issue_event_issue ON issue_event(issue_id, ts);

-- Persistent project memory (the `bd remember` analog).
CREATE TABLE memory (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  ts          TEXT NOT NULL,
  key         TEXT NOT NULL UNIQUE,                -- e.g. 'coding-style/no-linq-in-hot-paths'
  body        TEXT NOT NULL,
  ttl_days    INTEGER                              -- null = forever; otherwise auto-decay
);

-- Schema bookkeeping
CREATE TABLE schema_version (
  version     INTEGER PRIMARY KEY,
  applied_at  TEXT NOT NULL
);
```

## ID strategy

Today: caller-supplied string IDs (`--enqueue-task t-001`). Problem: doesn't survive a copy-paste from another project, doesn't encode ordering, two operators can pick the same id.

Beads approach: hash of `epoch + sequence + random`. Cheap to generate, collision-resistant, decodes back to ordering.

**Decision (2026-06-28): per-type sequence.** Single-writer orchestrator, no merge hazard, the readability win matters more for the dashboard. See "Open questions" below.

Our approach:

- `short_id` is a monotonic per-type sequence (`task-1`, `task-2`, …) — backed by SQLite `AUTOINCREMENT` on a per-type counters table so it survives restart.
- `id` is `<type>-<short_id>` (`task-1`, `pr-watch-1`, `memory-1`).
- CLI surface becomes `--enqueue "Add Position ECS component"` — caller gives a title, server assigns the id. `bd`-style hash IDs can be added later as an opt-in if we ever care about merge-safety from beads import.
- The current `--enqueue-task <name>` flag is kept as an alias: `<name>` becomes the title.

This is a small enough delta that we can do it in one PR with the migration.

## JSONL export

Mirror beads' approach exactly: a side-effect of every write is "rewrite `.portHorizon/state/issues.jsonl`." This file is:

- One JSON object per line, sorted by `id`.
- Generated under the same semaphore as the DB write, then atomic-renamed.
- Safe to `tail`, `grep`, commit to git for a "what was the queue?" history.
- **Not** the source of truth. On startup, the DB is canonical; the JSONL is a viewer artifact.

The dashboard gets a new `/api/issues.jsonl` endpoint that streams this file's tail, so you can `curl … | jq` or have a tiny CLI pager.

## Replacing `StateStore.Tasks`

Today:

```csharp
public record OrchestratorState(
    List<AgentTask> Tasks,
    DateTime LastHeartbeat,
    int CompletedTasks, int FailedTasks, int SchemaVersion = 2);

public record AgentTask(string Id, string Type, string Description,
    Dictionary<string,object> Parameters, string Branch,
    AgentTaskStatus Status = AgentTaskStatus.Pending, ...);
```

Tomorrow (Phase 1, what we land first):

```csharp
public interface IIssueStore
{
    Task<Issue> CreateAsync(NewIssue spec, CancellationToken ct);
    Task<Issue?> ClaimAsync(string id, string assignee, CancellationToken ct);     // atomic: claim iff status='open' && no blockers
    Task<Issue> TransitionAsync(string id, IssueStatus to, string? error, CancellationToken ct);
    Task<Issue> AddNoteAsync(string id, string body, CancellationToken ct);
    Task<IReadOnlyList<Issue>> ReadyAsync(int limit, CancellationToken ct);       // no open blockers, ordered by priority/created
    Task<IReadOnlyList<Issue>> ListAsync(IssueFilter filter, CancellationToken ct);
    Task<IReadOnlyList<IssueEdge>> DependenciesAsync(string id, CancellationToken ct);
    Task AddDependencyAsync(string from, string to, string kind, CancellationToken ct);
    Task SetMetadataAsync(string id, string key, JsonElement value, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> RememberAsync(string? key, string body, int? ttlDays, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> RecallAsync(string? keyPrefix, CancellationToken ct);
}
```

The `OrchestratorAgent` becomes:

```csharp
var ready = await _issues.ReadyAsync(_spawner.MaxConcurrentSessions, ct);
foreach (var issue in ready)
    _ = Task.Run(() => DispatchAsync(issue, ct), ct);
```

…and `DispatchAsync` calls `_issues.ClaimAsync(issue.Id, roleAgent.KiloAgentName, ct)` before doing any work. Two orchestrators on the same DB will fight cleanly for the ready queue instead of double-dispatching.

The dashboard stops reading `OrchestratorState`; it reads `/api/issues?status=...&assignee=...` instead. The SSE stream is repointed to emit `issue_event` rows (which is what `issue_event.kind = 'status_change'` already gives us).

`StateStore` doesn't get deleted in Phase 1 — it shrinks to "orchestrator settings + heartbeat + JSONL mirror" and keeps the atomic-write + schema-version + reaper patterns. Only the `Tasks` list moves out.

## Migration plan (no big-bang)

1. Add `Microsoft.Data.Sqlite` package reference.
2. Land `IssueStore` + schema migration `001_init.sql` alongside the existing `StateStore`. `IssueStore.CreateAsync` writes to both DB and JSONL; `OrchestratorAgent` ignores the DB for Phase 1 and keeps reading from `StateStore.Tasks` (so we can validate the new code path without changing dispatch behavior).
3. Add dashboard `/api/issues` + `/api/events` enrichment. Ship UI on a feature flag.
4. Flip the orchestrator's dispatch loop to claim from `IssueStore`. Keep `StateStore.Tasks` as a write-only mirror for one release so older dashboards keep working.
5. Delete `StateStore.Tasks` and `AgentTask`. Bump schema version to 3.

Each step is independently shippable and reversible.

## What I'm explicitly leaving out for v1

- Hierarchical IDs (epics).
- Cross-host sync (`bd dolt push/pull`-equivalent).
- Semantic compaction / memory decay. We log every `issue_event` but don't summarize. Add when the issue_event table gets big.
- Time-travel queries ("what was the queue state at 14:32?"). Beads gets this for free from Dolt commits; we'd add it with periodic snapshots if anyone asks.
- A web UI for editing issues. The dashboard is read-only for v1. Edit via CLI or by editing `issues.jsonl` and running `bd sync` (the import-from-JSONL command).
- Webhook ingestion. Dispatch loop polls the DB; no inbound HTTP.

## Comparison summary

| | Beads (external) | This proposal |
|---|---|---|
| Process model | `bd` CLI subprocess, optional Dolt server | In-process, single .NET assembly |
| Storage | Dolt (Git-for-SQL) or sqlite-via-Dolt | SQLite (WAL) |
| Multi-writer | Yes (with server mode) | No (single orchestrator, dashboard reads) |
| Sync | `bd dolt push/pull` | None (single host) |
| IDs | Hash (`bd-a1b2`) | Per-type sequence (`ph-1`, `task-1`) |
| Human export | `.beads/issues.jsonl` | `.portHorizon/state/issues.jsonl` |
| Memory | `bd remember` / `bd prime` | `memory` table + same read API |
| Cost to embed | Out-of-process binary, separate install | One NuGet package, one migration |

The point: same product surface (issue graph + claim + JSONL export + memory), one moving part instead of two, no Dolt.

---

## Open questions for the user

1. ~~**IDs.** Per-type sequence vs random hash.~~ **Resolved 2026-06-28: per-type sequence.** Single-writer orchestrator makes merge-safety irrelevant; readability wins.
2. **Memory TTL.** Project memory is forever by default in beads. Do you want auto-decay on some classes (e.g. "this insight is only relevant until next release") or is "forever, manually pruned" fine?
3. **Concurrency ceiling.** Single orchestrator, multi-orchestrator, or fan-out to multiple machines? Drives whether SQLite-WAL is enough or we need real replication.
4. **Dashboard read-only vs read-write.** Read-only ships faster. Read-write needs authn/authz thinking (who can create issues, who can close them).
5. ~~**Schema migration timing.**~~ **Resolved 2026-06-28: phase-by-phase, ship each independently reversible.** No big-bang PR.
