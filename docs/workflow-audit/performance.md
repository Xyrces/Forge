# UI/API performance audit — direction before implementation

Reviewed source: `8460dde0742640bb333c4540a255e1b7a3f4b2ce` on
`codex/unattended-workflow-review`. Live runtime is the qualified `1b733cb`
deployment, reached through the existing loopback SSH tunnel. This review changed
no application behavior, data, gates or host configuration and made no model calls.
It used eight serial GET requests, source inspection, and prior qualification
observations. No browser automation, database tracing or load test was performed.

**Recommended direction:** bounded, screen-specific read models plus one shared
refresh/loading policy. Sprints is currently a history export disguised as a
board fetch. Fix that data shape and redundant work before considering more
resources, larger connection pools, or generalized caching infrastructure.

## Measured small-dataset baseline

One HttpClient, sequential calls, elapsed time through the existing tunnel from
Windows to the Linux dashboard, including complete response-body read. HTTP 200
for every request. The dataset contains **two issue rows and one sprint**; the
spec list is empty. These are samples, not p95, server-only timing, or production
capacity claims. Browser rendering is not included. Initial connection/JIT/cache
effects and background operator browsing were not isolated.

| GET | Elapsed ms | Body bytes | Samples |
|---|---:|---:|---:|
| `/api/sprints?projectId=forge` | 247 | 781 | 1 |
| `/api/state?projectId=forge` | 377, 365, 368 | 54,271 each | 3 |
| `/api/tasks/dev-1?projectId=forge` | 238 | 18,214 | 1 |
| `/api/specs?project=forge` | 113 | 2 | 1 |
| `/api/now` | 444 | 907 | 1 |
| `/api/flow?projectId=forge` | 568 | 9,286 | 1 |

The actual Sprints page consumes `/api/state`, **not** the smaller `/api/sprints`
response (`Forge.UI/Features/View/ViewState.cs:143`). Three state samples have a
368ms median even at this tiny history. The current Now refresh performs its two
requests serially; these individual samples sum to approximately one second, but
that sum is not a separately measured page load.

## Findings and evidence

### P1: Sprint history drives board latency and query count

`Dashboard/DashboardHost.cs:312` reads all issues, the active sprint, all agents,
all skills and all sprints. Lines 342–347 then perform a separate membership read
for **every historical sprint**, before computing all rollups. Every issue's full
description/metadata is returned, and skill bodies are included at line 425.
`Core/SprintStore.cs:105` has no paging or date boundary. Sprints sorts the entire
list and shows a selected board (`Forge.UI/Components/Pages/Sprints.razor:55`);
the dropdown does not make its backing query bounded.

Static query count for the state handler is **5 + S + one blocker query when
membership is nonempty**, excluding project lookup/refresh overhead. S is the
total stored sprint count. This is source-derived, not measured SQL telemetry.
At 500 historical sprints, this is at least 505 sequential store reads for one
board refresh. Membership IDs from all sprints also feed a single unbounded
parameterized IN-list (`Core/IssueStore.cs:2675`); it is not chunked or deduplicated
at the call site. History growth therefore introduces a parameter-count failure
risk as well as latency. Do not fix the history query by issuing hundreds of SQL
requests in parallel.

**Direction:** fetch an active/selected sprint board with only its current members,
blockers and summary fields; paginate compact sprint history separately. Batch
membership/rollup SQL, include server counts, and fetch descriptions, metadata,
agent configuration and skill bodies only on their respective detail surfaces.

### P1: Refresh amplification can undo an otherwise fast endpoint

The board has a 30-second safety timer, initially firing after two seconds
(`Sprints.razor:622`), which fetches gates, queue, building state and the state
snapshot. Every SSE event, regardless of kind/project, schedules another reload
after 400ms (`:650`). That path fetches state, queue, building state and selected
sprint drafts (`:668`). The debounce cancels only the pending delay, not an
already-running reload. Fluxor effects use `CancellationToken.None` and have no
request generation/identity check (`Features/View/ViewEffects.cs:16`). A timer,
SSE burst, manual refresh and project switch can overlap, and a slower older
project response can overwrite a newer selection. This race is inferred from
control flow; it was not forced against the live service.

Now uses a five-second timer, sequential `/api/now` and `/api/flow`, and no
in-flight guard (`Now.razor:265`, `:281`). Each browser tab repeats these costs.
The previous live pilot separately observed roughly 1.5-second held-merge polling:
unchanged `CiGreen` observations triggered additional lifecycle work. The current
watcher reports `CiGreen` at `Reviewer/PRWatcher.cs:432`. That historical rate was
not remeasured here. Treat scheduler/event idempotence as a prerequisite; UI
debouncing alone cannot remove unnecessary GitHub/SQL activity.

**Direction:** one in-flight refresh per screen/project, coalescing one pending
refresh; cancellation and request-key checks on navigation; scoped SSE
invalidation; no-op suppression for unchanged lifecycle observations. Use a slow
safety refresh after event loss and visibility-aware polling, not a rapid timer
as the primary consistency mechanism. The sprint logic audit owns transition
semantics; performance must not weaken the state-machine audit.

### P1: Intake lists all conversation bodies before project filtering

`Core/IntakeStore.cs:137` selects all session headers; lines 162–168 then load every
session's messages in a separate query. `Dashboard/IntakeEndpoints.cs:29` applies
the requested project filter **after** this materialization. This is **1 + H**
queries for H sessions across the registry, even when viewing one project, and
returns complete conversations where a sidebar only needs summaries. The source
comment that per-session reads keep memory bounded is misleading: the result list
retains all loaded messages. This path was inspected, not timed with populated
session history.

The client subsequently fetches the selected session again, its specs, then one
full task-detail request per epic proposal (`Intake.razor:765`, `:782`). Each task
detail itself searches historical sprint memberships. Sidebar history and proposal
status therefore multiply backend work.

**Direction:** project-filtered, paginated session-summary query; separately paged
messages for the selected session; batched minimal epic-status projection. Keep
message history accessible without loading it into every session-list response.

### P2: Task/spec/Now queries repeatedly scan more data than displayed

- Task detail searches all sprint headers and then memberships until finding the
  task (`Dashboard/TaskEndpoints.cs:119`): **3 + R** core reads for task, events,
  sprint list and R inspected memberships, plus lookup overhead. Replace this
  with an indexed task-to-sprint lookup. Task events are capped at 100, but there
  is no continuation in this endpoint; preserve older audit access with paging.
- In-progress tasks are fully loaded before `.Take(limit ?? 100)` and each returned
  task adds an event query (`TaskEndpoints.cs:171`). Limit is not clamped there;
  pagination and maximums should be enforced server-side.
- Specs list joins every selected spec's complete current body with no page limit
  (`Core/SpecStore.cs:300`). Spec tree loads all project issues, then repeatedly
  filters them per story (`Dashboard/SpecEndpoints.cs:113`). Use compact spec list
  projections and one bounded parent-chain/subtree query for detail.
- Now loads all issues for each project (`NowEndpoints.cs:86`) and then one event
  query for up to 20 pending tasks per project (`:161`). Flow independently loads
  all specs and issues for counts/samples (`FlowEndpoints.cs:103`, `:110`), so the
  same five-second screen refresh repeats whole-history reads.
- Queue calls `ReadyAsync(0, ...)` with no result limit (`QueueEndpoints.cs:54`),
  whereas Sprints displays only eight items after downloading the complete result
  (`Sprints.razor:500`). Return the total separately from a bounded preview.

**Direction:** aggregate/summarize in SQL and batch related IDs. Share a small
project dashboard projection where fields genuinely overlap. Avoid a universal
giant snapshot or a cache that hides unbounded queries. Maintain correct project
ownership and historical access while limiting each screen's default work.

### P2: Loading/error behavior makes slow or failed reads look like workflow failure

- Now swallows all load errors (`Now.razor:298`); an initial error leaves the
  `Loading…` state indefinitely (`:17`), while later errors silently show old data.
- Intake converts list errors to an empty session list and active-session errors
  to empty messages (`Intake.razor:750`, `:778`). Users cannot distinguish outage
  from missing history. Queue/draft failures similarly become empty panels
  (`Sprints.razor:509`, `:530`).
- View reducers preserve data during background refresh, a useful existing design
  (`Features/View/ViewReducers.cs:8`), but Sprints' top-level error branch replaces
  the board even after a background failure (`Sprints.razor:43`).
- View, Tasks and Specs effects all fetch without cancellation or stale-response
  identity (`ViewEffects.cs:20`, `TasksEffects.cs:22`, `SpecsEffects.cs:20`). The
  Specs component creates/disposes a token source but does not pass it to its
  Fluxor fetch (`Specs.razor:198`).
- Task detail serially loads task, state, runs and project URL
  (`TaskDetail.razor:382`, `:398`, `:413`, `:446`); additive failures can hide
  evidence without a warning. Independent requests can be combined or bounded-
  parallelized after eliminating history scans. Do not parallelize mutations.

**Direction:** shared initial-loading / refresh-in-progress / stale-data / empty /
failure state with timestamps, request identity, retry and preserved last-good
content. A warning about unavailable auxiliary evidence is preferable to silently
showing an empty result. Root's UI journey audit owns presentation details.

## Offline qualification before changing the live service

1. Add a counting/delay-injecting database wrapper and typed store fakes. Record
   executed commands, not just HTTP count. Seed 1/100/1,000 sprints and
   10/1,000/10,000 tasks; use at least three projects with colliding task IDs.
   Assert selected-board query count is independent of unrelated history, payload
   size is page-bounded, and history remains reachable with stable cursors.
2. Seed 1/100/1,000 intake sessions with large messages across projects. Assert
   session-list queries do not retrieve message bodies or another project's
   sessions; selected-history pagination has no gaps/duplicates.
3. Exercise several thousand membership IDs and SQL Server-compatible batching/
   parameters on a disposable database. A passing SQLite test alone does not
   qualify Azure SQL limits. Do not mutate the live dataset for scale testing.
4. Introduce 25/100/500ms query latency offline and report command count, bytes and
   endpoint latency. Proposed targets for agreement: constant small query counts
   for active summaries, bounded page sizes and no request pileup; choose actual
   p95 budgets after representative measurements, not from these eight samples.
5. Deliver an SSE burst, a five-second request, overlapping timer/manual refresh,
   a project switch and out-of-order completions. Assert at most one active fetch
   per key, one coalesced refresh, disposal cancellation, and no cross-project
   stale response replacing the current screen.
6. Inject first-load and background 503/timeouts. Verify useful retry/error states,
   retained last-good data, honest timestamps and no false empty-history message.
7. Replay unchanged held `MergeReady`/`CiGreen` observations offline. Assert no
   repeated business-state write/event fanout or self-triggered polling loop;
   genuine head/status changes must still invalidate the relevant UI and watcher.

Implementation sequence to agree: **bounded sprint/lookup queries first; intake
summary/message split second; shared refresh/error policy alongside both; Now/
Flow aggregate reads next.** Add timings/query counters as diagnostics rather than
building a new observability platform. The audit does not request a larger host,
new cache service, benchmark spending, or production load generation.
