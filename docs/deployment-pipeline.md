# Deployment pipeline (P8)

Forge's dispatch loop can now target Forge's own repository as an
ordinary registered project — coding agents open PRs against
`Xyrces/Forge` the same way they would against any other project. This
document covers the other half of "self-maintenance": how a merged
change actually reaches the **running** Forge instance, and how an
operator gates that.

## Design goals

- **Merging is not deploying.** A burst of merges to a project's
  `main` branch never triggers an unplanned redeploy on its own.
  Deployment candidates only exist because an operator (or, later, an
  automated policy) explicitly requested one for a specific commit —
  which does not have to be the tip of any branch.
- **Per-project deployment strategy.** Not every project deploys the
  same way. A project might need nothing more than a git tag; Forge
  itself needs a full build → publish → service bounce. Each project
  picks a `DeploymentKind` independently (`Configuration/DeploymentOptions.cs`).
- **Deploying Forge is special-cased, not generalized.** Forge cannot
  overwrite its own open `.exe`/`.dll` files while running. Every
  other deployment kind runs inline, in-process, and can report
  success/failure synchronously. Only `SelfHostedWindowsService` needs
  the detached-helper dance described below.

## The `deployment` table

Schema v15 (`Core/IssueStore.cs`) added a `deployment` table, one row
per candidate, living in the same sqlite file as that project's
issues. `DeploymentPipeline/DeploymentStore.cs` is the typed access layer.

Status machine:

```
Pending ──(build check)──> BuildRunning ──> BuildPassed ──(approve)──> Approved ──> Deploying ──> Deployed
   │                                    └──> BuildFailed                                      └──> DeployFailed
   └──(project skips build check)──────────────────────────> Approved (same as above)
   Pending / BuildRunning / BuildPassed / BuildFailed ──(reject)──> Rejected  [terminal]
```

`Approve`/`Reject` are both atomic CAS writes (`DeploymentStore.TryApproveAsync`/
`TryRejectAsync`): the `UPDATE`'s `WHERE status IN (...)` clause re-checks status
at write time, not just at an earlier read, so two concurrent approve calls for
the same row can never both succeed, and rejecting a row that has already moved
past `BuildFailed` (e.g. `Approved`, `Deploying`, `Deployed`) is refused rather
than silently overwriting the outcome. The loser gets `409 Conflict`.

## Requesting a candidate

`POST /api/deployments/` `{ projectId, commitSha, requestedBy? }` (or
the **Deployments** dashboard page, which also exposes a
`GET /api/deployments/commits?projectId=` picker backed by `git log`
against that project's root — pick ANY commit, not just `HEAD`).

If the project's `DeploymentOptions.RequireBuildCheck` is `true`
(the default), `DeploymentPipeline/DeploymentBuildRunner.cs` immediately:

1. Checks the candidate commit out into an ephemeral, detached-HEAD
   git worktree at `{ProjectRoot}/.forge/deploy-checkouts/{deploymentId}`
   (separate from the branch-based worktrees `GitWorktreeService`
   manages for agent sessions — this one is always removed at the end
   of the run).
2. Runs `BuildCommand` then `TestCommand` (defaults: `dotnet build -c
   Release` / `dotnet test -c Release`; override per project for other
   stacks).
3. Records combined stdout+stderr in `build_log` and transitions the
   row to `BuildPassed` or `BuildFailed`.

When `RequireBuildCheck` is `false` (e.g. a tag-only deployment has
nothing to compile), the row is marked `BuildPassed` immediately with
a note explaining the skip.

## Approving a candidate

`POST /api/deployments/{id}/approve?projectId=<id>` `{ approvedBy?, force? }`.
Only `Pending` or `BuildPassed` rows are approvable. `projectId` is required —
the endpoint resolves the row within that project only and 404s otherwise, so
a deployment can never be approved/rejected by id alone from a mismatched
project. `POST /api/deployments/{id}/reject?projectId=<id>` follows the same
shape and works from `Pending`/`BuildRunning`/`BuildPassed`/`BuildFailed`.

**In-flight guard.** If the project's `DeploymentKind` is
`SelfHostedWindowsService` — i.e. this deployment bounces the ONE
Forge process that runs the dispatch loop for **every** registered
project, not just this one — the endpoint checks `SlotTable.Snapshot()`
across all projects first. Any non-zero `InFlight` count returns
`409 in_flight_tasks` with a human-readable message instead of
silently interrupting other projects' agent sessions. Pass `force:
true` to proceed anyway (the dashboard surfaces this as a "Force
deploy anyway" button after the first blocked attempt).

After approval, `DeploymentPipeline/DeploymentExecutorFactory.cs` picks the
executor for the project's `DeploymentKind` and runs it.

## `DeploymentKind.Script`

`DeploymentPipeline/ScriptDeploymentExecutor.cs`. Runs a configured script/command
in-process (`await`ed synchronously — the HTTP response carries the
final Deployed/DeployFailed verdict) with:

```
FORGE_DEPLOY_PROJECT_ID=<project id>
FORGE_DEPLOY_COMMIT_SHA=<commit sha>
FORGE_DEPLOY_PROJECT_ROOT=<project root>
```

in the environment. `ScriptPath` is resolved relative to the
project's root when not absolute. This is the right choice for
anything that isn't "redeploy Forge itself" — tagging a release,
kicking a Docker build, running `npm publish`, notifying a webhook,
etc. Example (`appsettings.multi-project.example.json`):

```json
"deployment": {
  "kind": "Script",
  "requireBuildCheck": false,
  "scriptPath": "scripts/tag-release.ps1"
}
```

## `DeploymentKind.SelfHostedSystemdService`

The path Forge uses to redeploy **itself** on Linux.
`DeploymentPipeline/SelfHostedSystemdServiceDeploymentExecutor.cs`:

1. Checks the candidate commit out into an ephemeral worktree (same
   pattern as the build runner).
2. `dotnet publish {PublishProject} -c Release -o {ReleasesRoot}/{sha}`
   — a brand new, never-before-used versioned directory. Nothing that
   could currently be `current` is ever touched.
3. Copies `Forge.UI/wwwroot/*` into the release dir (the static
   files `dotnet publish` doesn't ship).
4. Atomically repoints `{CurrentLinkPath}` at the new release
   directory: `ln -sfn` to a temp name + `mv -Tf` into place (POSIX
   rename is atomic on the same filesystem, so a `tail -f` or a
   concurrent agent run never sees a half-applied symlink).
5. `systemctl restart {ServiceName}`. The unit's ExecStart points at
   `/opt/forge/current/Forge.Core.dll`, so the restart picks up the
   freshly-repointed binary. `Type=notify` + `AddSystemd()` make the
   restart synchronous — `systemctl` blocks until READY=1 (i.e.
   until the new process has called READY=1 in `RunOrchestratorAsync`).

**No detached helper process** is needed, unlike the historical
Windows Service path. systemd's `stop`/`start` are reliable
operations on a third-party process — there's no "Forge killing its
own SCM registration" race to work around. Result: the executor
returns synchronously with success/failure already known; no result
file pickup on next startup, no `DeploymentResultReconciler`.

## Filesystem layout (systemd service mode)

```
/opt/forge/
  releases/<sha>/         <-- dotnet publish output, one per deployed commit
  current -> releases/<sha> <-- symlink, repointed on every successful deploy

/etc/forge/
  appsettings.json         <-- service's own config (NOT the dev repo's gitignored one)
  forge.env                <-- secrets (mode 0600, root:forge)

/var/lib/forge/            <-- StateDirectory=forge
  state/
    issues.db              <-- IssueStore (SQLite)
    memory.db              <-- MemoryStore (SQLite)
    issues.jsonl           <-- IssuesJsonlMirror
```

The dev repo (wherever `projects[].root` points, e.g.
`/home/jtn5016/repos/gamedev/Forge`) is completely separate from
this. Agents commit and open PRs there; nothing under `/opt/forge`
or `/var/lib/forge` is ever git-tracked.

## Installing the systemd service

```bash
# One-time, from root. Publish a first release manually before
# running this.
sudo dotnet publish Forge.Core/Forge.Core.csproj -c Release -o /opt/forge/releases/bootstrap

sudo scripts/install-systemd-service.sh \
    --release-dir /opt/forge/releases/bootstrap
```

The installer:
- Creates the `forge` system user (nologin, home `/var/lib/forge`).
- Drops secrets from `KILO_GATEWAY_API_KEY` / `GITHUB_TOKEN` env vars
  into `/etc/forge/forge.env` (mode 0600) when set.
- Repoints `/opt/forge/current` at the release dir.
- Copies `deploy/systemd/forge.service` into `/etc/systemd/system/`.
- `systemctl enable --now forge` + `systemctl status`.

Every deployment after that is the executor doing steps 1-5 above —
`install-systemd-service.sh` is a one-time setup step. The unit
template is `deploy/systemd/forge.service`; re-running the install
script with a new `--release-dir` repoints + restarts in place.

Full operator runbook (TLS, journald log queries, reverse-proxy
config, backup procedures): `docs/linux-deployment.md`.

## Known limitations

- **The whole service bounces, not just one project's dispatch.**
  Forge runs a single dispatch loop across every registered project in
  one process. A `SelfHostedSystemdService` deploy for the `forge`
  project restarts that entire process, which is why the approve
  endpoint checks in-flight tasks across ALL projects, not just
  `forge`'s own slots.
- **Linux-only.** `SelfHostedSystemdService` is Linux-only by
  construction (`OperatingSystem.IsLinux()` guard at the top of the
  executor). For non-Linux hosts, use `Script` and a hand-rolled
  install script.
- **No authentication on `/api/deployments` (or anywhere else in the
  dashboard).** Mitigated today by `AgentOptions.Hostname` defaulting
  to `127.0.0.1` — the dashboard is not reachable off-box out of the
  box. If an operator changes `Hostname` to bind on a network
  interface, put a reverse proxy with real authentication in front of
  it first; there is no in-app auth to fall back on.
- **`Forge.Deployer` dying mid-flight is detected, but only after a
  wait.** `DeploymentResultReconciler` marks a `Deploying` row
  `DeployFailed` once it's been stuck for 10 minutes with no result
  file — long enough to not misfire on the ordinary "service just
  restarted, give the helper a few seconds" case, short enough that an
  operator isn't staring at a row that will never move. There's still
  no automatic retry or rollback; a failed self-hosted deploy needs a
  fresh candidate + approval to try again.
- **Rollback is manual.** To roll back, request a deployment for an
  older commit sha and approve it — there's no dedicated "rollback to
  previous release" button yet; old `releases\<sha>` directories are
  kept indefinitely (not garbage collected) precisely so this works.
