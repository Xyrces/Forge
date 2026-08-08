# Talaria messaging migration — Forge goes message-driven

## Goal
Replace Forge's timer/poller-driven internal coordination with Talaria messages (in-memory transport first, Azure Service Bus later), and migrate the composition root to Microsoft DI everywhere. GitHub remains the only polled external system (no push channel; Talaria has no delayed messages yet).

## Decisions (operator-approved 2026-08-08)
1. **Full conversion now** — all internal loops become message handlers; timers survive only as 15m backstops + GitHub-truth polling.
2. **Sagas deferred.** TaskStateMachine (DB-backed) stays the sole transition authority. Operator will spec a Talaria extension point (`ISagaTransitionAuthority<TState>` + custom `IStateStore` over Forge's DB) in the Talaria repo; task-lifecycle saga migration is a follow-up plan after ServiceBus transport + extension point exist.
3. **DI everywhere.** Full composition-root migration in this plan: stores, factories, schedulers, OrchestratorAgent, PRWatcher, dashboard services all registered in the container, constructor-injected. Hand-wiring deleted. `Program.cs` already has `Host.CreateApplicationBuilder` (line 139).
4. **Core event seam.** `IEventPublisher` interface + pure-record event contracts in Core (same pattern as the `Core/Db` provider seam; `NullEventPublisher` default). Stores publish after mutations — publication cannot be skipped by a forgetful caller. Talaria-backed implementation in a new `Messaging/` module. **No Talaria reference in Core.**
5. **Package source: GitHub Packages** under Xyrces. NuGet restore from GitHub Packages requires a PAT even for public repos → prerequisite nuget.config + credentials on dev box, CI, and the systemd host.
6. **15m backstops.** Watch sweep, groomer, designer, artist, assembler relax to 15m tick messages. Watchdog stays 15m, orphan reaper 30m, JSONL mirror 5s (local viewer artifact, cheap).

## Working agreements (operator directive 2026-08-08)
- **Feature branch only.** All work on `feature/talaria-messaging` cut from `main`; land via PR (repo branch protection — main is protected, no direct commits). Commit at checkpoints per `process.commit_at_checkpoints`.
- **NO deploy into the running environment until the operator explicitly authorizes it.** Do NOT `dotnet publish` to `~/.local/share/forge/releases/dev`, do NOT `systemctl --user restart forge`, do NOT touch the live service or the production Azure SQL state DB. The implementation session ends at "PR open + local validation green".
- **No local run against production state.** The live service is the only orchestrator permitted against the Azure SQL state DB. Local end-to-end validation uses a throwaway SQLite state dir (config override) and `--check`/`--once`, never the production DB.

## Prereqs (before any code)
- [ ] Talaria nugets on GitHub Packages: `Talaria.Core` + `Talaria.Transports.InMemory` (net10 target exists — repo multitargets net8/9/10).
- [ ] `nuget.config` in Forge: `github-xyrces` source (`https://nuget.pkg.github.com/Xyrces/index.json`), PAT via env var on the dev box + CI secret. (Systemd-host PAT only needed later, at authorized deploy time.)
- [ ] Cut `feature/talaria-messaging` from `main` before first commit.

## Architecture
- **Contracts (`Core/Messaging/`)**: pure records, no dependencies — `TaskTransitioned`, `TaskEnqueued`, `PrOpened`, `ReviewVerdictRecorded`, `SpecStatusChanged`, `FollowUpFiled`, `GroomRequested`, `SweepTick` (watch/groom/design/artist/assemble kinds). Every record carries `ProjectId`; `TaskId`/`SpecId` as applicable; `MessageId` deterministic (e.g. `{taskId}:{state}:{stateEnteredAt:O}`) so Talaria idempotency dedupes double-publication.
- **Seam (`Core/Messaging/IEventPublisher.cs`)**: `Task PublishAsync<T>(T evt, CancellationToken ct)`; `NullEventPublisher.Instance` default. Stores take it via ctor (optional param, null → Null) so Core tests stay dependency-free.
- **`Messaging/` module** (new top-level dir, depends on Core + Talaria.Core + Talaria.Transports.InMemory): `TalariaEventPublisher` (topic-per-event-type, `partitionKey = projectId`), `EventConsumer<T>` BackgroundService base (consume → handler → Commit; fault → Nack + log, never swallow), topic-name constants. Transport instance created once from config (`messaging.transport=inmemory` default; `servicebus` reserved) and registered as singleton.
- **Wiring**: `Program.cs` registers the whole graph in the host builder's `ServiceCollection` (stores as singletons via existing factories, schedulers/watchers/orchestrator as hosted services or singletons started explicitly). `DashboardHost`'s separate `WebApplication` container gets the same transport instance + `IEventPublisher` registered as instances so endpoints can publish.

## Handler rules (non-negotiable)
- **Messages are hints, not truth.** Every handler re-reads DB state and is idempotent; anything derivable is re-derivable after restart (StartupRecovery unchanged; in-memory messages lost on crash is fine).
- No business logic in publishers; stores publish *after* the mutation commits.
- Consumers use `ExecutorFaultGuard`-style fault logging (MAF InProcessExecution swallows faults — lesson from PRWatcher).
- Consumers must not block: long work (agent runs) stays in the existing run registry; handlers only kick.

## Ordered tasks
1. **nuget.config + package refs** (`Talaria.Core`, `Talaria.Transports.InMemory`) in the main csproj (`Forge.Core/Forge.Core.csproj` — globs `..\**\*.cs`).
2. **`Core/Messaging/`**: event records + `IEventPublisher` + `NullEventPublisher`.
3. **`Messaging/` module**: `TalariaEventPublisher`, `EventConsumer<T>` base, transport factory (`messaging.transport`), topic constants.
4. **DI composition-root migration**: move Program.cs hand-wiring into `ServiceCollection` (stores, slot table, factories, `OrchestratorAgent`, `PRWatcher`, all schedulers, watchdog, reaper, mirror, `StartupRecovery`, groomer/intake factories). DashboardHost: pass-through registration of shared instances. Delete hand-wired ctor chains. Keep `--check`/`--status`/`--once` CLI paths working (they resolve from the container now).
5. **Store instrumentation**: `IssueStore` (Create/Claim/Transition/UpdateMetadata → `TaskEnqueued`/`TaskTransitioned`), `SprintStore` (sprint activated/completed), `SpecStore` (`SpecStatusChanged`), follow-up store (`FollowUpFiled`). All fire-and-forget-safe: publish failures log + swallow (never break a DB mutation over a hint).
6. **Dispatch loop**: replace `PollIntervalSeconds` sleep with event-signaled wakeup (channel signaled by `TaskEnqueued`/`TaskTransitioned`/run-finished) + 15m backstop tick.
7. **Watch pipeline**: `PrOpened` (from CommitPushPr) → launch background review immediately; `ReviewVerdictRecorded` → merge attempt now (replaces the `_nextWatchSweepUtc=MinValue` early-sweep hack added today); `TaskTransitioned→MergeReady` → immediate GitHub poll of that PR only. Sweep itself becomes `SweepTick(watch)` consumer (15m publisher) running the existing MergeReady-first sequential sweep — unchanged logic, GitHub truth.
8. **Schedulers**: groomer (spec-groom + ad-hoc), designer, artist, assembler each consume their trigger events + `SweepTick` backstop at 15m; internal 5m `PeriodicTimer`s deleted. `ScheduledWatchdog` (15m) and `OrphanedClaimReaper` (30m) stay plain timers (their job is time-based detection, not reacting to events).
9. **Cooldowns**: LLM/GitHub rate-limit cooldown timers stay internal (not bus material).
10. **Tests**: contracts round-trip (System.Text.Json), `TalariaEventPublisher` against `InMemoryTransport`, consumer commit/nack semantics, store-publishes-after-mutation tests, dispatch-loop wakeup test, end-to-end: enqueue task → handler wakes without timer. Update tests that construct stores (new optional ctor param).
11. **Docs**: AGENTS.md module-boundary table (+Messaging/, +Core seam rule "stores publish after mutations"), README state/flow notes.
12. **PR + hold**: open PR from `feature/talaria-messaging` → `main`. Stop there. **Do not deploy.** Post the local-validation results in the PR.
13. **Deploy (ONLY on explicit operator authorization)**: publish to `~/.local/share/forge/releases/dev`, `systemctl --user restart forge`, verify live: sprint assembles on enqueue (no 15m wait), PR review launches on PrOpened, merge lands on verdict, backstop tick logs every 15m.

## Failure modes / risks
- **GitHub Packages PAT missing on host** → restore fails at publish; prereq check before coding.
- **Event handler throws before Commit** → Nack → redelivery/DLQ (`*.dlq` topics exist in InMemoryTransport); handlers must tolerate redelivery (idempotency rule).
- **Bounded channel full (default capacity)** → producer waits; if a consumer dies the pipeline stalls silently → consumer hosted services must heartbeat/log; watchdog extended to alert on consumer death.
- **Double-publish** → deterministic MessageId + Talaria in-memory idempotency store dedupes.
- **Machine suspend** → no messages flow during sleep; on wake, 15m backstops re-sync (same as today).
- **Two containers (host + DashboardHost WebApplication)** sharing one transport instance — thread-safety is fine (channel-based, SingleWriter=false), but must be registered as the *same instance*.

## Out of scope
- Talaria sagas / `MapSaga` / saga state stores (follow-up plan after Talaria extension point + ServiceBus transport).
- GitHub webhooks (the real endgame for PR polling).
- ServiceBus transport implementation/config (lands in Talaria; Forge flips `messaging.transport` when ready).
- `IDashboardEventBus` / SSE (UI-scoped, stays as-is).
- JSONL mirror (stays 5s timer).
- DurableDispatcher (P4 Stage B path) — untouched.

## Validation
- **Local (pre-PR, always allowed)**: full suite green (1239 today) + new messaging tests; `dotnet build` clean (TreatWarningsAsErrors); `--check` against a throwaway SQLite state dir; local end-to-end against that SQLite dir (`--once` or short service run): enqueue task → handler wakes without timer; PR opened → review kicks; verdict → merge attempt; restart → StartupRecovery replays; 15m backstop tick visible in logs.
- **Live (post-authorization only)**: same scenarios observed on porthorizon production after the authorized deploy.
