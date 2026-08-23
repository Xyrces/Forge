# Project-flag liveness + QA-stage reachability fix

## Why (incident, verified live 2026-08-23)

`$qa`/`$triage` are `enabled:true` in the live registry for porthorizon (verified via direct
Azure SQL read of `core.project.roles_json`), but the running process behaves as if both are off:

- client-1 / PR #1040 ran ClientDev + 2× Reviewer and **merged with zero QA runs, no qaVerdict**
  — exactly what the watch-lane QA stage (#109/#110) exists to prevent.
- `GET /api/triage/ledger?projectId=porthorizon` reports `triageEnabled:false`; no
  `TriageRequested` will ever fire.

Three distinct defects, all confirmed in code:

### D-a — Watch-lane bundle mapping drops the flags (primary cause)
`Orchestrator/Consumers/WatchConsumers.cs:41-50` (`WatchConsumerBase.BundleForAsync`) builds
`ProjectOptions` from the store record but copies only `Id/Name/RepoUrl/DefaultBranch/Root/Roles` —
`TriageEnabled`, `QaEnabled`, `Territories`, `VerifyCommands` are silently dropped. Every watch
path (SweepTick, PrOpened fast path, TaskTransitioned) goes through this, so the watch lane sees
`QaEnabled=false` **always, even across restarts**. `ProjectRecord` already parses the flags
(`Core/ProjectStore.cs:321-322`); the mapping just doesn't carry them.

### D-b — `ProjectContextFactory` cache never refreshes options
`Projects/ProjectContext.cs:197` (`Find`) returns the cached `ProjectContext` whose
`ProjectOptions` snapshot is from first `Find` (process boot). Flag PUTs update the DB but nothing
refreshes the snapshot until restart. Stale readers: `Dashboard/TriageEndpoints.cs:177`
(`ctx.Options.TriageEnabled` — the /triage banner toggle displays wrong state),
`Orchestrator/Consumers/FailureTriageConsumer.cs:120`, `Orchestrator/Consumers/TriageConsumer.cs:55`.
(`KnownProjects` itself re-reads the store per call and IS fresh; role caps are live via direct
SlotTable application in `PutRolesAsync`; plan-gate territory is fresh via
`ProjectTerritoryLookup` reading `KnownProjects`.)

### D-c — QA launch unreachable when a review is already current (latent deadlock)
In `Orchestrator/WatchSweepService.cs:267-290` (`TryLaunchBackgroundReviewAsync`), the QA branch
sits AFTER the `ShouldLaunchReview` early-return. A task whose review verdict is already current
at the head (e.g. reviewed while the flag was off/stale) never reaches the QA branch → no QA ever
launches → the merge gate (`Reviewer/PRWatcher.cs:358`, requires `qaVerdict=pass` at head when
enabled) holds forever with nothing able to open it. Silent deadlock.

## Scope decision (operator-confirmed)

This phase = the liveness + reachability fix + observability + live re-exercise. Failure-triage
phase 3 (model escalation, auto issue creation) stays out of scope per the phase-2 non-goals.
client-1's missing QA evidence needs NO separate follow-up: epic-11
(`spec-ebac8af413f54c3dbac4fa11bc55435d`, currently ReadyForDesign) scope item 3 redoes Tier-1
evidence by driving paint mode through the real player path including the paint entry point.

## Tasks (ordered)

1. **Fix D-a — carry the flags through the watch bundle mapping.**
   `WatchConsumers.cs` `BundleForAsync`: add `Territories`, `VerifyCommands`, `TriageEnabled`,
   `QaEnabled` to the `ProjectOptions` initializer from the record. (Verify the
   `ProjectOptions` property names match `ProjectRecord`'s; both live in Core.)

2. **Fix D-b — refresh-on-read in `ProjectContextFactory.Find`.**
   - Make `ProjectContext.Options` refreshable (private setter + an internal
     `RefreshOptions(ProjectOptions)` method; `ProjectOptions` is an immutable record so a
     reference swap is atomic enough for these read-mostly consumers).
   - In live mode (store-backed), `Find` re-resolves the project from `KnownProjects` on every
     call and swaps the fresh snapshot into the cached context; static mode is a no-op.
   - Do NOT dispose/recreate the cached context (it owns the shared `IssueStore`).
   - One factory instance is shared by orchestrator + dashboard (`ForgeComposition.cs:96` →
     `DashboardHost.cs:237`), so this fixes both sides.

3. **Fix D-c — QA-due evaluation independent of review currency.**
   In `TryLaunchBackgroundReviewAsync`, evaluate QA BEFORE the `ShouldLaunchReview` early-return:
   if `bundle.Project.QaEnabled` and QA is not current at the task's head
   (`qaSha != branchSha` or `qaVerdict` empty, from task metadata) and `ShouldLaunchQa` allows →
   `TryLaunchBackgroundQaAsync` and return (review waits). When QA IS current, fall through to
   the normal review path. Keep the reviewer self-skip (`ReviewerDispatcher.cs:151`) as
   defense-in-depth and the existing post-pass review relaunch in
   `TryLaunchBackgroundQaAsync`'s continuation. `QaDispatcher.VerifyOnceAsync` already dedupes on
   head sha internally.

4. **Observability — make flag state visible without raw SQL.**
   - Add `TriageEnabled`/`QaEnabled` to the project DTO in `GetProjectAsync` (and
     `ListProjectsAsync` if the DTO is shared) — sourced from the (now fresh) `ctx.Options`.
   - Project drill-down UI: show both flags as pills with toggles calling the existing
     `PUT /api/projects/{id}/triage` and `/qa` (caps already live in drill-down per the
     UI-consistency rule; cross-link the /triage banner toggle to the same surface rather than
     duplicating state). Follow `app.css` classes, no inline styles.

5. **Tests (xUnit, hand-rolled fakes, no mocking frameworks):**
   - `ProjectContextFactory`: flag flip in the fake `IProjectStore` is visible via
     `Find(...).Options` on the next call without restart (both directions).
   - `WatchConsumerBase.BundleForAsync`: mapping carries `TriageEnabled`/`QaEnabled`/
     `Territories`/`VerifyCommands`.
   - `WatchSweepService`: with `QaEnabled` and a review verdict already current at the head but
     no QA verdict → QA launches and review does not; with QA current+pass → review launches;
     no double-launch when QA already current.
   - Keep existing `PRWatcherQaGateTests` green.

6. **Validation:** full `dotnet test` suite green; main project builds clean under
   `TreatWarningsAsErrors`; `dotnet run --project Forge -- --check` passes.

7. **Rollout (branch protection applies):** feature branch → PR → review/merge via the normal
   pipeline. **No deploy without explicit operator authorization in-session** (this box is the
   live host). Post-deploy live verification, in order:
   - `GET /api/triage/ledger?projectId=porthorizon` shows `triageEnabled:true` (deploy restart
     loads the DB flags; then PUT off→on and confirm it flips WITHOUT a restart).
   - Next porthorizon PR: QA run appears in `/api/agent-runs?taskId=...` BEFORE the reviewer,
     `qaVerdict/qaSha` metadata lands, merge is held until `qaVerdict=pass` at the head.
   - /triage banner toggle + drill-down pills render real flag state.

## Failure modes / risks

- Refresh-on-read adds one registry `SELECT` per `Find` — `KnownProjects` already pays this per
  call and is documented as cheap at dashboard cadence.
- A consumer holding a long-lived `ProjectOptions` reference keeps its snapshot for that
  operation only — acceptable (per-tick staleness is inherent to any cache; flags flip
  seconds-to-next-read, not instantly).
- D-c's QA-first ordering means a task with stale QA but current review delays re-review until QA
  reruns — intended: reviewing an unverified head wastes a round (same rationale as the existing
  self-skip).

## Out of scope

- Failure-triage phase 3 (model escalation, automatic issue creation).
- Merge-gate semantics changes; SlotTable cap handling (already live).
- porthorizon-side evidence redo mechanics (owned by epic-11).
