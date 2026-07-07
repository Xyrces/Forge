# Multi-Project & Slot Dispatcher (v1)

## Why

Forge was hard-coded to a single `workspace.root` in `appsettings.json`.
Multi-project mode lets one orchestrator process track multiple codebases
side-by-side, expose per-project resource caps (the "slots"), and surface
cross-project state. v1 ships the **read-only multi-project dashboard**
plus the in-process **slot semaphore pool**. The legacy
**single-workspace dispatch loop is unchanged** for v1 — the multi-project
surface is dashboard introspection; the v2 cutover replaces the dispatch
loop.

## Decisions recap

| Decision | Resolution | Reason |
|---|---|---|
| Isolation model | Multi-tenant in one process; DB per project | Lets us split a project to its own process later without migration |
| Project registry | `appsettings.json` `projects[]` array (no CRUD in v1) | Static source of truth at boot |
| Agent "instance" | Slot = concurrency cap (in-process `SemaphoreSlim`) | Cheaper than per-task processes |
| Cap scope | Per-project `N` per role (no global `NN` in v1) | Global cap = v2 |
| Queue | None in v1; backpressure FIFO designed for v2 | Burst mode is sufficient when slot count > steady-state throughput |
| Migration | Back-compat: legacy `workspace.root` → synthetic `id="default"` | Don't break Playwright-verified PortHorizon setup |

## Where things live (new in v1)

```
Configuration/
  ProjectsOptions.cs       # ProjectsOptions + ProjectOptions + DefaultProjectRoles
  ProjectRegistryLoader.cs # Loads registry; legacy shim to "default"
  ProjectStateDirs.cs      # StateDirFor(p) / IssuesDbFor(p) / MemoryDbFor(p)
Orchestrator/Slots/
  SlotTable.cs             # in-process slot semaphore pool
Dashboard/
  ProjectsEndpoints.cs     # GET /api/projects, /api/projects/{id}, PATCH slots, GET /api/board
Projects/
  ProjectContext.cs        # ProjectContext + ProjectContextFactory (lazy, cached)
appsettings.multi-project.example.json   # operator-facing example for the new schema
```

## Operator quickstart

### Step 1 — Inspect your existing config

```bash
curl http://127.0.0.1:4097/api/projects/
```

A single-project setup with the legacy `workspace.root` returns:

```json
[{
  "id": "default",
  "name": "Default",
  "root": "C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon",
  "roles": {},
  "pending": 0, "inProgress": 0, "completed": 0, "failed": 0,
  "slots": [
    {"projectId":"default","role":"artist","inFlight":0,"max":1},
    ...
  ]
}]
```

A warning is logged at startup:
> Legacy workspace.root='...' detected; synthesizing single project id='default'. Migrate to projects[] to silence this warning.

### Step 2 — Add a second project (without breaking the first)

Copy `appsettings.multi-project.example.json` → `appsettings.json` (or merge
into your existing file). Restart the orchestrator. The legacy
`workspace.root` becomes a second entry implicitly unless you omit it.

```json
{
  "projects": {
    "projects": [
      {
        "id": "porthorizon",
        "name": "PortHorizon",
        "root": "C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon",
        "skillPlaybookUrl": "https://github.com/Xyrces/godot-ecs-gamedev-playbook",
        "roles": { "coredev": 2, "clientdev": 2, "reviewer": 2, "intake": 1, "designer": 1, "artist": 1 }
      },
      {
        "id": "suikoden-launcher",
        "name": "Suikoden Launcher",
        "root": "C:\\Users\\jtn50\\repos\\gamedev\\SuikodenLauncher",
        "roles": { "coredev": 1, "clientdev": 2, "reviewer": 1, "artist": 2 }
      }
    ]
  }
}
```

### Step 3 — Verify

```bash
curl http://127.0.0.1:4097/api/projects/
```

Returns 2 projects. Each has a counter row + slot list.

### Step 4 — Adjust slots at runtime

```bash
# Bump portHorizon's coredev cap to 5
curl -X PATCH http://127.0.0.1:4097/api/projects/porthorizon/slots/coredev \
     -H "Content-Type: application/json" -d '{"max":5}'

# Shrink suikoden-launcher's artist pool to 1 (live)
curl -X PATCH http://127.0.0.1:4097/api/projects/suikoden-launcher/slots/artist \
     -H "Content-Type: application/json" -d '{"max":1}'
```

The new cap is honored by new acquires; existing in-flight holders keep
running until they release.

## Dashboard surface

| Route | Purpose |
|---|---|
| `/projects` | Card grid: project, root, counters, slot chips |
| `/projects/{id}/overview` | Detail view: counters, slot utilization bars, role caps |
| `/board` | Cross-project kanban feed (status filter + project filter chips) |

The legacy routes (`/backlog`, `/sprints`, `/tasks`, `/agents`, `/skills`,
`/specs`, `/designs`, `/art`, `/intake`, `/vision`, `/ops/{memory,cost,recovery}`)
are **still served from the legacy single-workspace path**. They reflect
the `default` project's data. Future (v2) work will projectify them onto
`/projects/{id}/<tab>`.

## State dir layout

```
{root}/
  .portHorizon/
    state/
      issues.db              # default project (id=="default")
      memory.db              # default project memory
      issues.jsonl           # mirror tail
      porthorizon/           # future: per-project, when default no longer uses the flat layout
        issues.db
        memory.db
      suikoden-launcher/
        issues.db
        memory.db
```

**Important:** the v1 layout keeps the legacy `default` project at the
flat `.portHorizon/state` path to preserve the existing on-disk DBs and
test fixtures. Adding `projects[]` with non-`default` ids creates
**separate** state subdirs.

## Back-pressure (deferred to v2)

The schema is shaped for it but the queue table doesn't exist in v1:

- Per-project FIFO queue table (`project_id`, `issue_id`, `enqueued_at`).
- Slot exhaustion → enqueue instead of discard.
- A separate projector task that round-robins across projects' queues.

## Known v1 limitations

- **The orchestrator dispatch loop still uses the legacy single-workspace
  path.** Multi-project is dashboard-only. The v2 cutover replaces the
  dispatcher and per-project code paths in `OrchestratorAgent`.
- **No CRUD** on projects at runtime (edit `appsettings.json` and restart).
- **`/backlog`, `/sprints`, `/tasks`, etc. are NOT projectified** — they
  show the `default` project's data. Use `/board` to see other projects.
- **No global `NN` cap** — each project's caps are independent.
- **No multi-tenant auth** — anyone with HTTP access sees every project.
