# Multi-Project & Slot Dispatcher (v1) + Forgesystem bootstrap (v1.1)

## Why

Forge was hard-coded to a single `workspace.root` in `appsettings.json`,
and that path was expected to **already exist on disk as a git repo**
before the orchestrator started. v1 made the dashboard multi-project-aware.
**v1.1 (this commit) goes further: the orchestrator no longer assumes any
specific folder exists.** On a fresh machine, the AppData / XDG data home
is the canonical storage location; the bootstrap creates the project root
and `git init`s it if it's missing. Operators can still point projects at
an existing checkout via `workspace.root`, and can also point *the data
root* itself at a custom path via `forgesystem.dataRoot`.

## v1.1 — decisions recap

| Decision | Resolution | Reason |
|---|---|---|
| Where do per-project files live by default? | AppData / XDG / `~/Library/Application Support` | Fresh-machine installs need zero preconditions |
| Can the operator change the AppData location? | Yes: `forgesystem.dataRoot` (absolute path) | Mounted drives, dev-machine portability |
| Where do operator-supplied projects put state? | Inside their existing Root, under `.forge/state/<id>` (or `.portHorizon/state` for legacy `id="default"`) | Operator-managed repo + Forge state under one folder |
| Where does pre-flight scaffold a missing `workspace.root`? | Auto-creates the dir + runs `git init -b main` + commits a `.gitignore` | Operator CLI flow: nothing to do but `dotnet run` |
| `WorkspaceOptions.Root` validation | No longer required | Tied to the auto-scaffold step |

## Where things live (new in v1 + v1.1)

```
Configuration/
  ProjectsOptions.cs        # ProjectsOptions + ProjectOptions + DefaultProjectRoles
  ProjectRegistryLoader.cs  # Loads registry; legacy shim to "default"
  ProjectStateDirs.cs       # StateDirFor(p) / IssuesDbFor(p) / MemoryDbFor(p)
  ForgesystemOptions.cs     # NEW v1.1 — DataRoot override (in AgentOptions.Forgesystem)
  ForgesystemPaths.cs       # NEW v1.1 — ResolveDataRoot() + project layout helpers
Orchestrator/Slots/
  SlotTable.cs              # in-process slot semaphore pool
Projects/
  ProjectContext.cs         # ProjectContext + ProjectContextFactory (lazy, cached)
  ProjectBootstrap.cs       # NEW v1.1 — create root + git init + state dir per project
Dashboard/
  ProjectsEndpoints.cs      # GET /api/projects, /api/projects/{id}, PATCH slots, GET /api/board
appsettings.multi-project.example.json   # operator-facing example
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
| `/deployments` | P8: request/approve/reject deployment candidates per project — see `docs/deployment-pipeline.md` |

The legacy routes (`/backlog`, `/sprints`, `/tasks`, `/agents`, `/skills`,
`/specs`, `/designs`, `/art`, `/intake`, `/vision`, `/ops/{memory,cost,recovery}`)
are **still served from the legacy single-workspace path**. They reflect
the `default` project's data. Future (v2) work will projectify them onto
`/projects/{id}/<tab>`.

## State dir layout (v1.1)

Two modes:

### Mode A — operator-managed project with an explicit `root`

**Correction (verified against `Projects/ProjectBootstrap.cs` directly,
2026-07-07):** state does **not** live inside an operator-supplied
`root` for anything except the legacy `id="default"` project. Every
other project — whether its `root` was hand-set by the operator (e.g.
`projects[].root`) or left empty for auto-scaffolding — gets its state
centralized under the Forgesystem **data root**, keyed by project id.
This keeps Forge-owned sqlite files out of arbitrary operator repos
(no risk of an agent accidentally `git add`ing `issues.db`) and gives
every project's state a single, predictable location for backup/ops
tooling regardless of where its source tree happens to live.

```
{DataRoot}/projects/{id}/.forge/
  state/
    issues.db              # Forge-owned; deployment table lives here too (v15)
    memory.db
    issues.jsonl
  worktrees/                # NOT used by the dispatch loop in v1/v1.1 (see below)
  art-output/               # Meshy / Artist outputs
```

`GitWorktreeService` (agent task worktrees) is the one exception that
DOES live under the project's actual `root`, at `{root}/.forge/worktrees/`
— it's wired from the legacy `Workspace.WorktreeRoot` config, relative
to `primary.Root`, independent of `ProjectBootstrap`'s (currently
unused by the dispatch loop) `WorktreeParent` field. This is what lets
Forge maintain itself: the `forge` project's `root` points at the real
dev clone (e.g. `C:\Users\jtn50\repos\gamedev\Forge`), agent worktrees
land inside it as `.forge/worktrees/agent/<taskId>/` and get committed
+ PR'd normally, while the project's issues/memory/deployment state
stays out of that repo entirely.

**Exception:** the legacy `id="default"` project (synthesized when
`projects[]` is empty and `workspace.root` is set) keeps the v0 layout
(`{root}/.portHorizon/state/issues.db`) for full compatibility with
existing on-disk DBs and test fixtures.

### Mode B — fully auto-scaffolded project (v1.1 — zero config)

When no `projects[]` and no `workspace.root` is set, Forge creates the
project under the **Forgesystem data root**:

| OS | Default data root |
|---|---|
| Windows | `%LOCALAPPDATA%\Forge` |
| macOS | `~/Library/Application Support/Forge` |
| Linux | `$XDG_DATA_HOME/forge` (or `~/.local/share/forge`) |

Operators can override via `forgesystem.dataRoot` in `appsettings.json`
(absolute path; parent dirs created automatically).

```
{DataRoot}/
  projects/
    default/
      .git/
      .gitignore
      .forge/
        state/
          issues.db
          memory.db
        worktrees/
        art-output/
```

The bootstrap is **idempotent**: a second run finds `.git/`, skips the
`init`, and only allocates the state dir if missing.

### Env override

`FORGE_DEFAULT_PROJECT_ROOT=/path/to/git-repo` (read in
`ProjectRegistryLoader`) takes precedence over `workspace.root` for the
synthesized `default` project. Same intent as the legacy field, but
doesn't pollute the committed `appsettings.json`.

## Self-hosting: Forge as one of its own projects (P8)

There's nothing project-registry-specific about Forge's own repo — it
registers exactly like any other entry in `projects[]`:

```json
{ "id": "forge", "name": "Forge", "root": "C:\\Users\\jtn50\\repos\\gamedev\\Forge", "roles": { ... } }
```

Once registered, agents open PRs against whatever `github.owner`/`repo`
points at for that Forge process (matters if you keep the source repo's
own remote and the deploying instance's `github` config in sync). The
part that's genuinely new is what happens **after** a PR merges — see
`docs/deployment-pipeline.md` for the full deployment-approval flow
(`/deployments` dashboard page, `DeploymentKind.SelfHostedWindowsService`,
and the `Forge.Deployer` helper that survives the service bounce Forge
can't survive on its own).

**Ordering matters if you also list other projects.** The dispatch
loop (as opposed to the dashboard, which is genuinely multi-project) is
still single-workspace under the hood — it only ever runs against
`knownProjects[0]` (see "Known v1 + v1.1 limitations" below). List
`forge` first in `projects[]` if you want self-maintenance issues
actually dispatched, not just visible on `/board`; every other listed
project stays dashboard-readonly until the v2 dispatcher cutover.

## Back-pressure (deferred to v2)

The schema is shaped for it but the queue table doesn't exist in v1:

- Per-project FIFO queue table (`project_id`, `issue_id`, `enqueued_at`).
- Slot exhaustion → enqueue instead of discard.
- A separate projector task that round-robins across projects' queues.

## Known v1 + v1.1 limitations

- **The orchestrator dispatch loop still uses the legacy single-workspace
  path.** Multi-project is dashboard-only. The v2 cutover replaces the
  dispatcher and per-project code paths in `OrchestratorAgent`.
- **No CRUD** on projects at runtime (edit `appsettings.json` and restart).
- **`/backlog`, `/sprints`, `/tasks`, etc. are NOT projectified** — they
  show the `default` project's data. Use `/board` to see other projects.
- **No global `NN` cap** — each project's caps are independent.
- **No multi-tenant auth** — anyone with HTTP access sees every project.
- **Forgesystem `dataRoot` cannot be migrated.** Changing it after a
  fresh-machine install creates a new project root; back-compat is
  handled only on the legacy `workspace.root` field path.
- **Forgesystem bootstrap is git-only.** v1.1 calls `git init`; operators
  who want a Mercurial / pijul / subversion-backed project must wire that
  in themselves by `cd`'ing into the project dir + running their tool
  (the orchestrator will then ignore the absence of `.git` and let you
  run `git` commands only against git repos).
