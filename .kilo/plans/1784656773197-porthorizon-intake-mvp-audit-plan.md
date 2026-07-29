# PortHorizon MVP-audit → intake end-to-end test

Feed the PortHorizon MVP-gap audit (critical-path items 1–12 + 4 client defects) into intake and let the full pipeline decompose it into several sprints of work. Before that can succeed: fix a blocking per-project territory defect, activate the kimi provider, and clear the stale attention inbox.

## Preconditions discovered (2026-07-29, read-only investigation)

1. **BLOCKER — plan-territory gate is Forge-shaped.** `Agents/RoleAgentRegistry.cs:30` hardcodes coredev territory to `Core/, Agents/, Orchestrator/, Dashboard/, Configuration/, Projects/, AgentTools/, Reviewer/, DeploymentPipeline/, tests/, tools/, deploy/, scripts/, .github/, docs/, agents/, .kilo/` + root files; clientdev to `Forge.UI/, tests/`. `PlanTerritoryGate` rejects any plan naming `PortHorizon.Core/…` or `PortHorizon.Tests/…`. Proven live: porthorizon **task-7 (deflake) Failed** at 17:29 — plan REVISE ×2 then REJECTED, "PortHorizon.Tests/Systems/MaterialReservationSystemTests.cs is outside coredev's territory". The hygiene epic (task-1..6) only survived because it touched `docs/` + repo-root files. Every real porthorizon engineering run will fail this gate.
2. **Kimi key not live.** `Program.ResolveProviderApiKeysAsync` resolves `<provider>_api_key` from the secret store **at boot only**. Boot at 17:21:18 resolved only `kilo-gateway`; kimi_api_key was added after. `GET /api/agents/providers/kimi/models` → `[]` (unauthenticated 401 from api.moonshot.ai; endpoint itself reachable — no-auth probe returns 401 in 0.3s, correct OpenAI shape). Restart picks it up. Config already has the kimi provider (`https://api.moonshot.ai/v1`, default `kimi-k3`).
3. **Attention inbox is computed, not dismissible** (`Dashboard/Now/NowFeed.cs` — derives from issue state + gates). "Clearing notifications" = closing the stale Failed/Blocked issues: forge store task-6 (blocked, breaker), task-9..task-26 (401 PAID_MODEL_AUTH_REQUIRED era), task-38, task-39 (blocked, breaker); porthorizon store pr-watch-3 (legacy Failed watch row). task-7 is requeued (Phase 3), not closed.
4. **Porthorizon otherwise healthy**: all 3 sprints Completed, queue empty except Failed task-7 + terminal pr-watch rows, `lastSyncError` null on both projects, no live runs.

## Decisions

- **Model overrides (user-chosen): Reviewer + Intake → `kimi|kimi-k3`.** Dev/QA stay on `minimax/minimax-m3` (mandate). The plan-llm-review critic follows the Reviewer resolution; groomer/designer inherit CoreDev (unchanged) — the factory resolves overrides per AgentType (`OpenAICompatibleChatClientFactory.cs:81`), so these are the only clean targets.
- **Territory fix: per-project territory in `project.roles_json`** (same DB payload that already carries role caps, `PUT /api/projects/{id}/roles`). No schema migration — roles_json is a JSON column; the parsed role entry gains optional `territoryPrefixes` (string[]) + `territoryAllowsRootFiles` (bool?) fields. Resolution: project roles_json territory → `RoleAgentRegistry` default. Do NOT ship `agents/` or `.kilo/` config into the porthorizon repo (operator correction: never commit Forge-specific content to non-Forge repos).
- Porthorizon territory seed: coredev → `PortHorizon.Core/`, `PortHorizon.Tests/`, `PortHorizon.Benchmarks/`, `docs/`, `.github/` + root files; clientdev → `PortHorizon.Client/`, `PortHorizon.Tests/`.
- task-7 requeued after the territory fix as the end-to-end validation (its plan must pass the gate with porthorizon paths).

## Ordered steps

### Phase 0 — Health check + clear inbox
1. Journal scan (2h) for 429s, stateViolation, unexpected errors; confirm no held gates (`GET /api/gates`).
2. Close stale attention issues via the operator issue PATCH (`?projectId=porthorizon` where needed), status=Closed with note "stale — superseded era / cleared by operator": forge task-6, task-9..task-26, task-38, task-39; porthorizon pr-watch-3. Verify `GET /api/now` attention is empty.

### Phase 1 — Per-project territory fix (code)
3. Extend the roles_json role-entry model with optional territory fields (find the roles_json parse seam near `DefaultProjectRoles` / the `PUT /api/projects/{id}/roles` handler); add a territory resolver (project override → `RoleAgentRegistry` default); thread it into `MafAgentRunner`'s `RunGateContext` construction (run context already carries `projectId` from the skills work).
4. Tests: resolver fallback + override-wins; `PlanTerritoryGate` approving `PortHorizon.Tests/…` under seeded porthorizon config; roles endpoint round-trip preserving caps. Full build + suite green. Commit, push, wait CI green.
5. Publish + restart (this restart also picks up kimi — do Phase 2 verification next).
6. Seed porthorizon territory via `PUT /api/projects/porthorizon/roles` (merge: preserve existing caps).

### Phase 2 — Kimi activation
7. Verify boot log "provider 'kimi' api key resolved"; `GET /api/agents/providers/kimi/models` returns non-empty.
8. `PUT /api/agents/roles/Reviewer/model` and `.../Intake/model` = `{provider:"kimi", model:"kimi-k3"}`; verify `/api/agents` shows override source for both.

### Phase 3 — Validate with task-7
9. Requeue task-7 (operator requeue path). Watch: plan gate approves porthorizon paths; run completes; PR opens; state-driven watch picks it up (no pr-watch row); kimi reviewer verdict lands; merge. Any plan-gate territory reject = fix not effective, stop and diagnose.

### Phase 4 — Feed the audit to intake
10. `POST /api/intake/sessions` `{projectId:"porthorizon"}` with a structured brief: goal = "integration spine to playable MVP"; the 12 critical-path items with sizes and evidence paths (WorldManager bootstrap, 19 unregistered systems, ship-JSON csproj glob, GridSyncSystem dead flag, sprite components, construction tick driver, blueprint input wiring, HUD/scene nodes, crew component seeding, ship physicalization, conduit UI, save/load registration) + the 4 headless-boot client defects; note items are mostly wiring, and ask for decomposition into multiple coherent epics (expected ~3: bootstrap+wiring, construction/blueprint loop, ships/utilities — but accept intake's own split if sane).
11. Drive the conversation (`POST /api/intake/sessions/{id}/messages`) to epic proposal(s); accept via `POST /api/intake/sessions/{id}/accept-epic/{messageId}`.

### Phase 5 — Monitoring (err on too-frequent checks)
12. Check every few minutes through each stage: specs → designer → groomer (**stories must land in the porthorizon store** — `?projectId=porthorizon` on state API; cross-store rows = regression of the routing invariant, stop) → sprint assembly → dispatch → plan gate → PRs → kimi reviews → rework/merge. Watch for: kimi 429s (ModelRateLimitTracker is per provider+model — kimi cooldown must not freeze minimax dev runs), breaker trips, sprint-coherence violations, slot contention.
13. Report at each stage transition. No manual out-of-loop fixes — surface anomalies to the operator.

## Validation
- `GET /api/now` attention empty; task-7 merged via state-driven watch with kimi reviewer verdict in task metadata.
- Intake session produces accepted epics; groomed stories appear in porthorizon store only; new sprint assembles; ≥1 new-work task completes plan-gate → PR → kimi review → merge.
- Forge CI green on the territory-fix commit.

## Risks / notes
- kimi rate limits/quotas unknown — first heavy reviewer use may 429; tracker should isolate it, verify live.
- Intake may propose one mega-epic — acceptable; designer/groomer decomposition is part of the test.
- Territory editing is API-only in this pass; surfacing territory in the project drill-down UI is a follow-up (UI-consistency rule: don't create a second editor later — extend the existing caps surface).
- QA/Reviewer roles have no territory prefixes (read-only) — unaffected by the fix.
