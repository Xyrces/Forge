# Deploy 33a26a9 (flag liveness + QA reachability + triage phase 3) to live + seed + verification

## Precondition

**Operator authorization required in-session** (this box is the live Forge host;
`deploy.live_deploy_forbidden`). The runbook ends at a restarted, seeded, verified service.

## Current state (verified 2026-08-24 07:50)

- main = **33a26a9** (PR #113 phase 3, on top of f515cc5 PR #112). One deploy carries both.
- Live service runs release **48** (`task-type-routing-48`, started Aug 23 18:09 EDT) — pre-#112.
- Live flags in DB: porthorizon `$triage`/`$qa` = enabled:true; runtime still reports
  `triageEnabled:false` (stale cache, pre-fix). Gates: all open. No active agent runs.
- Live ledger: 2 open failures; signatures now include `breaker-exhausted` ×1 (new since #112).
- epic-11 still `ReadyForDesign` (designer lane will move it post-deploy).
- Old binary 404s `PUT /api/agents/roles/{designer,groomer,artist}/model` (AgentTypes don't exist
  pre-#113) and crash-loops on pre-staged `llm.roles.Designer` config → **the seed cannot be
  pre-staged; it happens in the gate-held window after restart**.
- Deploy mode: user-mode systemd (`systemctl --user`), `current` symlink into
  `~/.local/share/forge/releases/`, port 443 via CAP_NET_BIND_SERVICE (unit re-applies).
- Next release number: **49**.

## Deploy steps (ordered — gate hold FIRST)

1. `git checkout main && git pull` — confirm HEAD = 33a26a9.
2. Confirm nothing mid-flight: `GET /api/agent-runs` → `active` empty (verified 07:50).
3. **Hold the pipeline gates** so no designer/groomer/artist run fires during the unseeded window
   (post-restart, pre-seed they would run on provider default — inheritance is cut in this build):
   `POST /api/gates/design/hold`, `POST /api/gates/groom/hold`, `POST /api/gates/sprint/hold`
   (sprint hold stops new dispatch too; merge gate stays open so in-flight watches settle).
4. Publish:
   `dotnet publish Forge.Core/Forge.Core.csproj -c Release -o ~/.local/share/forge/releases/triage-escalation-49`
   (verify the dir doesn't exist first).
5. Pre-flight the NEW binary (no restart):
   `~/.dotnet/dotnet ~/.local/share/forge/releases/triage-escalation-49/Forge.Core.dll --config ~/.config/forge/appsettings.json --check`
   — must exit 0 (known gap: export `GITHUB_PACKAGES_*` in the shell first).
6. Repoint + restart:
   `ln -sfn ~/.local/share/forge/releases/triage-escalation-49 ~/.local/share/forge/current`
   `systemctl --user restart forge`
7. Health: `systemctl --user status forge` active; `journalctl --user -u forge -n 100` shows
   READY + all consumers (one per topic) + no schema errors; https://127.0.0.1/ answers.

## Seed (task 6 of the phase-3 plan — inside the gate-held window)

PUT project-scoped model overrides for designer/groomer/artist = the CURRENT effective coredev
resolution per project (recorded live 07:50 from `/api/agents/roles?projectId=`):

| project | seed value (provider/model) | source today |
|---|---|---|
| porthorizon | `minimax` / `MiniMax-M3` | coredev project override |
| forge | `minimax` / `MiniMax-M3` | coredev global override |
| talaria | `kimi` / `kimi-for-coding` | coredev project override |

Per cell, three PUTs (`designer`, `groomer`, `artist`) to
`PUT /api/agents/roles/{name}/model` with body
`{"provider":"<p>","model":"<m>","projectId":"<project>"}` — 9 calls total.
Then verify: `GET /api/agents/roles?projectId=<p>` shows all three roles with
`source: override (project)` and the seeded values.

**Release the gates** (`POST /api/gates/{design,groom,sprint}/release`) only after the seed
verifies. If the deploy is rolled back BEFORE seeding, release the gates immediately — release 48
still inherits coredev, so no seed is needed there.

## Rollback

`ln -sfn ~/.local/share/forge/releases/task-type-routing-48 ~/.local/share/forge/current && systemctl --user restart forge`
Safe: no schema migration in #112/#113 (new memory-key namespaces only; old binary ignores them).
If rollback happens post-seed, the seeded `llm/roleModel/<project>/Designer|Groomer|Artist` keys
are harmless on the old binary (unknown AgentTypes are never resolved there).

## Live verification (ordered; pass/fail)

| # | Check | Pass criteria |
|---|-------|---------------|
| V1 | `GET /api/triage/ledger?projectId=porthorizon` | `triageEnabled:true` immediately post-restart |
| V2 | D-b fix: `PUT /api/projects/porthorizon/triage {"enabled":false}` → re-GET | flips `false` with NO restart; PUT back `true`. Leave both flags ON |
| V3 | UI: drill-down "Stage flags" pills + `/triage` banner cross-link | pills render true/true; screenshot recorded |
| V4 | QA-before-review: next porthorizon PR — `GET /api/agent-runs?taskId=<id>` | `role=QA` run BEFORE Reviewer; `qaVerdict/qaSha` metadata; merge held until `qaVerdict=pass` at head. Wait for a natural PR (epic-11 lane is moving) — do not force-dispatch |
| V5 | Triage agent: next NEW failure-boundary crossing for porthorizon | `actor=triage` row on the ledger (requeue/park/flag). Pre-existing 2 open rows do NOT retro-fire |
| V6 | Seed verified (above) + `GET /api/agents/roles` | designer/groomer/artist listed with own models; NOTHING inherits (labels show override/config, never coredev's source) |
| V7 | Escalation (operator sets target first): `PUT /api/agents/roles/coredev/escalation-model` with the operator's chosen provider+model; then on a triage action | `escalate_model` on the ledger; next run labeled with the escalated model + `modelEscalated` metadata; marker gone post-dispatch; escalated run draws the `escalated:coredev` pool only (normal coredev pool untouched); a second escalated coredev task waits |

## Failure modes

- `--check` fails on the new binary → do NOT restart; release the gates; fix forward or stay on 48.
- Service fails READY after restart → rollback, release gates, diagnose from journal.
- Seed partially applied → gates stay held; re-PUT the missing cells (idempotent).
- Restart during an active run orphans it (StartupRecovery replays) — step 2 avoids this.

## Out of scope

- Choosing the escalation target model (operator decision at V7 time).
- porthorizon-side work (epic-11 flows on its own); ProductAgent model editability (phase-3
  follow-up candidate); failure-triage phase 4.
