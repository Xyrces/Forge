# Deploy f515cc5 (flag liveness + QA reachability) to live + post-deploy verification

## Precondition

**Operator authorization required in-session** (this box is the live Forge host;
`deploy.live_deploy_forbidden`). The runbook ends at a restarted, verified service.

## Current state (verified 2026-08-23 19:43)

- main = f515cc5 (PR #112 merged 23:40Z). Live service runs the pre-fix build
  (`releases/task-type-routing-48`, started 18:09 EDT).
- Live flags in DB: porthorizon `$triage`/`$qa` = enabled:true; runtime still reports
  `triageEnabled:false` (stale cache, pre-fix).
- Deploy mode: **user-mode systemd** — unit `forge.service` under `systemctl --user`;
  `ExecStart=$HOME/.dotnet/dotnet ~/.local/share/forge/current/Forge.Core.dll --config
  ~/.config/forge/appsettings.json`; `current` is a symlink into `~/.local/share/forge/releases/`.
  Port 443 via CAP_NET_BIND_SERVICE (unit re-applies it; dotnet binary unchanged, so no cap work).
- Release dir convention: `<slug>-<n>`; highest existing is 48 → next is **49**.

## Deploy steps

1. `git checkout main && git pull` — confirm HEAD = f515cc5.
2. Check nothing is mid-flight (a restart kills in-flight agent runs):
   `GET /api/agent-runs` → `active` empty; glance at `/api/now`. If runs are active, wait.
3. Publish:
   `dotnet publish Forge.Core/Forge.Core.csproj -c Release -o ~/.local/share/forge/releases/flag-liveness-49`
   (verify the dir doesn't already exist first).
4. Pre-flight the NEW binary against the live config without restarting:
   `~/.dotnet/dotnet ~/.local/share/forge/releases/flag-liveness-49/Forge.Core.dll --config ~/.config/forge/appsettings.json --check`
   — read-only (config + schema + GitHub + gateway auth); must exit 0. Known pre-existing
   gap: shell-local `GITHUB_PACKAGES_*` env may need exporting first.
5. Repoint + restart:
   `ln -sfn ~/.local/share/forge/releases/flag-liveness-49 ~/.local/share/forge/current`
   `systemctl --user restart forge`
6. Health: `systemctl --user status forge` active; `journalctl --user -u forge -n 100`
   shows READY + all consumers started (one per topic) + no schema errors; dashboard
   answers on https://127.0.0.1/.

## Rollback

`ln -sfn ~/.local/share/forge/releases/task-type-routing-48 ~/.local/share/forge/current && systemctl --user restart forge`
(safe: no schema change in #112; v36 predates it).

## Live verification (ordered; each has pass/fail)

| # | Check | Pass criteria |
|---|-------|---------------|
| V1 | `GET /api/triage/ledger?projectId=porthorizon` | `triageEnabled:true` immediately post-restart (DB flags loaded at boot) |
| V2 | D-b fix live: `PUT /api/projects/porthorizon/triage {"enabled":false}` → re-GET ledger | flips to `false` with NO restart; PUT back to `true` → `true`. Leave both flags ON at the end |
| V3 | UI: project drill-down "Stage flags" pills + `/triage` banner cross-link | pills render true/true against real data; screenshot recorded (operator rule: verify UI live) |
| V4 | QA-before-review (D-a + D-c): next porthorizon PR — `GET /api/agent-runs?taskId=<id>&projectId=porthorizon` | a `role=QA` run appears BEFORE the Reviewer run; task metadata gains `qaVerdict/qaSha`; merge held until `qaVerdict=pass` at head. Requires a PR in flight (sprint 142 is active); if none materializes, leave pending — do not force-dispatch |
| V5 | Triage agent first action | Fires only on NEW failure-boundary crossings (no retroactive kick for the 2 pre-existing open rows). Watch ledger for `actor=triage` rows; the operator may requeue a failed task to force a crossing sooner. Guardrails (≤2/day/task) apply |

## Failure modes

- `--check` fails on the new binary → do NOT restart; fix forward or stay on 48.
- Service fails READY after restart → rollback immediately (above), then diagnose from
  `journalctl --user -u forge`.
- Restart during an active agent run orphans it — StartupRecovery replays on boot, but
  step 2 avoids this entirely.

## Out of scope

- Failure-triage phase 3 (separate plan doc).
- Any porthorizon-side work (epic-11 flows through the pipeline on its own).
