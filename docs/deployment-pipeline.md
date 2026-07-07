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

## `DeploymentKind.SelfHostedWindowsService`

The path Forge uses to redeploy **itself**. `DeploymentPipeline/SelfHostedWindowsServiceDeploymentExecutor.cs`:

1. Checks the candidate commit out into an ephemeral worktree (same
   pattern as the build runner).
2. `dotnet publish {PublishProject} -c Release -o {ReleasesRoot}\{sha}`
   — a brand new, never-before-used versioned directory. Nothing that
   could currently be `current` is ever touched.
3. `dotnet publish tools/Forge.Deployer/Forge.Deployer.csproj -c
   Release -o {ReleasesRoot}\..\deployer` — republishes the helper
   tool itself from the SAME commit, into a **stable** folder outside
   the release/current rotation (so the helper isn't subject to the
   same file-lock problem it exists to solve).
4. Launches `Forge.Deployer.exe` **detached** with `--service-name
   --current-link --release-dir --result-path`, then returns
   immediately with the row left at `Deploying`. This request is
   expected to return successfully even though Forge's own process is
   about to be killed a few seconds later by the very thing it just
   launched.

`Forge.Deployer` (`tools/Forge.Deployer/Program.cs`) is a standalone
console app with **zero** dependency on `Forge.Core` — if it
referenced Forge.Core's assemblies, a locked-file race would be
possible during the exact swap it exists to perform. It:

1. Stops the named Windows Service (`ServiceController.Stop()` +
   `WaitForStatus(Stopped)`).
2. Repoints the `current` **junction** (not a symlink — junctions
   don't need `SeCreateSymbolicLinkPrivilege`, just ordinary
   filesystem write access) at the new release directory. `mklink /J`
   via `cmd.exe`, since .NET's `Directory.CreateSymbolicLink` creates
   real symlinks, not junctions.
3. Starts the service back up and waits for `Running`.
4. Writes a `{success, releaseDir, log, completedAtUtc}` JSON result
   file to `--result-path` and exits.

### Closing the loop: `DeploymentResultReconciler`

Forge.Core's own process died mid-flow (step 1 above), so nothing
inside that process can ever record the final Deployed/DeployFailed
verdict. `DeploymentPipeline/DeploymentResultReconciler.cs` runs once at the START
of every `RunOrchestratorAsync` — i.e. as soon as ANY Forge.Core
process boots, whether that's the just-deployed release starting
cleanly or an operator manually restarting after a failed swap — and:

1. For every project with `DeploymentKind.SelfHostedWindowsService`,
   scans `{ReleasesRoot}\..\deploy-status\*.json`.
2. Matches each result file's name (the deployment id) against that
   project's `DeploymentStore`, writes `MarkDeployedAsync`/
   `MarkDeployFailedAsync` with the captured log, and deletes the file.

## Filesystem layout (Windows Service mode)

```
C:\ProgramData\Forge\
  current\              <-- junction, repointed on every successful deploy
  releases\
    <sha-a>\             <-- dotnet publish output, one per deployed commit
    <sha-b>\
  deployer\              <-- stable Forge.Deployer.exe, republished on every deploy
  deploy-status\         <-- transient result files, consumed by DeploymentResultReconciler
  appsettings.json        <-- service's own config (NOT the dev repo's gitignored one)
```

The dev repo (wherever `projects[].root` points, e.g.
`C:\Users\jtn50\repos\gamedev\Forge`) is completely separate from
this. Agents commit and open PRs there; nothing under
`C:\ProgramData\Forge` is ever git-tracked.

## Installing the Windows Service

```powershell
# One-time, from an elevated PowerShell session. Publish a first
# release manually before running this (the script refuses to
# register a service pointed at a missing binary).
dotnet publish Forge.Core.csproj -c Release -o C:\ProgramData\Forge\releases\bootstrap
cmd /c mklink /J C:\ProgramData\Forge\current C:\ProgramData\Forge\releases\bootstrap

scripts\install-service.ps1 -CurrentLink C:\ProgramData\Forge\current `
                             -AppSettings C:\ProgramData\Forge\appsettings.json
```

Every deployment after that repoints `current` and restarts the
service — `install-service.ps1` is a one-time setup step, not part of
the deploy loop. `scripts\uninstall-service.ps1` reverses it (stops +
`sc.exe delete`; does not touch `releases`/`deploy-status`).

## Known limitations

- **The whole service bounces, not just one project's dispatch.**
  Forge runs a single dispatch loop across every registered project in
  one process. A `SelfHostedWindowsService` deploy for the `forge`
  project restarts that entire process, which is why the approve
  endpoint checks in-flight tasks across ALL projects, not just
  `forge`'s own slots.
- **Junction-based, Windows-only.** `Forge.Deployer` is not built or
  runnable on non-Windows platforms; `DeploymentKind.SelfHostedWindowsService`
  is a Windows Service concept end to end.
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
