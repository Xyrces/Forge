# Bounded sprint board and history reads

Design and implementation record, 2026-11-17. Implemented in isolated `codex/sprint-board-performance` from budget/UI commit `567473d2ddb2e994aaa2b9f5f8e99ca555d5994f`; not deployed. This changes read paths and loading behavior, not sprint planning, initiative approval, dispatch eligibility, or operator authority.

## Implemented qualification

`Core/SprintReadStore.cs` supplies dedicated summary/board projections: three history queries and four queries for a nonempty board, regardless of historical sprint count. `/api/queue?limit=8` uses a bounded active-sprint preview; the existing unlimited queue and dispatch paths retain their behavior. Sprints no longer consumes `ViewState` or calls `/api/state`; Backlog and other consumers are unchanged.

Production Release build passed with zero warnings/errors. Forty focused tests passed (`SprintReadStoreTests`, `QueueEndpointTests`, `ProjectLensEndpointTests`, `SprintStoreTests`, `DashboardContractTests`). Evidence includes command-counting before/after 500 historical sprints, traversal of 2,500 members with bounded pages and exact totals, failed/open blockers and external-to-page boost, typed HTTP project isolation with colliding issue ids, and stale-selection cancellation. These tests use SQLite and local HTTP. Independent Azure SQL qualification subsequently passed against exact reader source `d53bfd6864bd631da35a7b950faf06d6de34653a`: 23 history rows, 230 paged members, eight external failed blockers with a five-id preview, queue boost order, same-id isolation across two schemas, foreign-cursor rejection, and cancellation. Both disposable schemas were removed; production tables were not written. The private receipt is `.portHorizon/sprint-sql-qualification/result.jsonl` in the main review worktree. A rendered browser check remains pending. No paid model call was required.

The integrated performance and activation branch (`codex/sprint-workflow-safety`, based on `1c54c63`, performance pick `bce7897`, activation pick `bfffac1`) passed 129 focused Release tests and a production Release build with zero warnings/errors. This combined local qualification does not substitute for SQL Server activation-write/concurrency qualification or deployment proof.

Exact counts still require aggregate database work and header ordering may sort history. Existing build-state and draft secondary panels remain separate snapshot endpoints, not newly claimed bounded exports. Refreshes are coalesced; project/selection generations discard stale responses. Multi-statement reads are live projections, not frozen audit snapshots; a changed active sprint is handled by checking the queue's sprint id against the displayed board.

## Problem and preserved behavior

Sprints currently loads `ViewClient.FetchAsync` (`Forge.UI/Features/View/ViewState.cs:143`), which exports `/api/state`. `Dashboard/DashboardHost.cs:319` reads every issue, agents, full skills, every sprint, then serially calls `GetIssueIdsAsync` once per sprint. Thus 500 historical sprints add 500 membership queries even when the operator views only the active sprint. `SprintStore.ListAsync:105` has no pagination. The current page then joins these full issue rows back into its cards (`Sprints.razor:556`).

Preserve the current default selection: active sprint, otherwise newest sprint; explicit historical selection remains selected until the operator changes it. Preserve goal, dates, status, exact total/completed counts, lifecycle-based columns, assignment, role, PR/rework badges, blockers, server-computed situation, project links, and active-sprint queue. Never make a page of cards look like the entire sprint. Backlog and other `ViewState` consumers continue using their existing contract; do not trim `/api/state` globally.

## Minimal API contract

Add a dedicated `Dashboard/SprintReadEndpoints.cs`, mapped alongside existing sprint endpoints. Require a registered `projectId`; an unknown project/sprint returns 404, never the primary project's data. Keep the existing `/api/sprints`, `/api/sprints/{id}/issues`, and `/api/state` responses unchanged.

| Request | Bounded result |
|---|---|
| `GET /api/sprints/summary?projectId=...&limit=20&cursor=...` | `projectId`, `generatedAt`, `activeSprint` header or null, exact `totalSprintCount`, up to 20 historical headers, `nextCursor` or null. Maximum limit 50. Headers have id/name/status/dates; no members, issue metadata, or large goal body. |
| `GET /api/sprints/board?projectId=...&sprintId=...&limit=100&cursor=...` | `projectId`, `generatedAt`, selected sprint header including goal, exact `issueCount`/`doneCount`, up to 100 card rows, and `nextCursor`. Maximum limit 200. Omitted sprintId selects active, otherwise newest. Empty project returns `sprint:null`, not an error. Explicit missing sprint is 404. |

Summary returns the active header separately, so it is selectable even while paging older history. History excludes the active row; deduplicate by id when displaying it. Stable history ordering is `(start_date DESC, id DESC)` to preserve existing date semantics with a deterministic tie-breaker. Encode and validate both fields in the opaque cursor. Do not use an ever-growing offset or trust cursor-supplied project ids.

Board members use stable `(created_at ASC, id ASC)` keyset ordering. Membership changes can occur between requests; refresh starts a new page chain. A continuation is tied to its project/sprint; reject a cursor for another selection. The client retains a bounded current page rather than concatenating every page forever. Page controls explicitly say “Showing X–Y of N tasks”; counts/progress refer to the entire sprint. Columns sort the loaded page using current presentation rules, with wording that column counts cover the displayed page. Typical small sprints remain visually unchanged. If per-column independent paging is later desired, it is a separate UI choice, not necessary for this fix.

`SprintCard` fields: id, title, type/role, status, lifecycleState, priority, assignee, prNumber, reworkAttempts, blockedBy identifiers, blockerCount, boosted, situation, situationTone. No description, worktree path, full metadata, transcript, prompt, agent catalog, or skill content crosses this endpoint. A bounded blocker preview (for example five ids plus count) prevents a malformed/high-degree dependency graph creating an unlimited response. Missing metadata uses current `TaskSituation` fallbacks, not invented lifecycle state.

## Store queries and boundaries

Add a concrete read-only `Core/SprintReadStore.cs` taking `IDbConnectionFactory`, or extend concrete `SprintStore` with a small read interface. Prefer the separate reader to avoid adding dashboard methods to every hand-written `ISprintStore` fake. It has database I/O only; endpoint composition handles project lookup, role mapping and DTO formatting. No new cache, service, broker, background projector, persisted projection, schema bump, or migration is required initially.

Summary has a fixed three-query budget after project resolution:

1. Read active header (`status='Active'`, capped at one).
2. Count sprint headers for the project store.
3. Fetch `limit + 1` historical headers using the keyset predicate and provider `TopParam`/`LimitParam`; the extra row determines `nextCursor`.

Board has a fixed query budget (target four; no per-member/per-history loop):

1. Resolve the explicit sprint, or choose active/newest in one ordered query capped at one.
2. Aggregate engineering member count and Completed/Closed count through `sprint_issue JOIN issue WHERE sprint_id=@id`. Apply the same exclusion of containers and legacy pr-watch rows as the existing board. Missing linked issues do not inflate counts.
3. Fetch `limit + 1` selected-sprint members with a SQL-side limit and stable keyset. Project only columns needed by cards. For the initial implementation, selected-page metadata may be read internally to reuse `TaskSituation.Describe`; it must not be serialized. Measure pathological metadata separately and, if necessary, select only its required keys with provider-specific JSON expressions rather than silently truncating semantics.
4. Fetch blockers/boost flags for those bounded page ids in one query (or two fixed queries if clearer). Use a join or bounded parameter list, not all members from every historical sprint. Preserve `Failed` as an open blocker. Boost must consider relevant dependencies even when the counterpart is outside the displayed page; do not derive it solely from visible cards.

Use parameterized SQL and the existing SQL dialect seam. `sprint_issue(sprint_id, issue_id)` and its sprint index already exist (`IssueStore.cs:457`, SQL Server path `:1251`). History sorting without a composite index can still scan/sort header rows; constant command count is not a claim of constant database CPU. Capture SQL plans/latency before proposing any additional index, which would require both migration paths and separate release qualification.

The backend may read bounded page metadata for situation text, but neither issue history nor all historical sprint membership is materialized. Do not call `IssueStore.ListAsync(new IssueFilter())` then take a page in memory. Do not call `GetAsync` for each member. Do not calculate total counts from the page length.

## UI implementation

Add a dedicated typed client/state under `Forge.UI/Features/Sprints/`; remove only Sprints' dependency on universal `ViewState`. Keep the existing cards and shared visual classes, PR-link helper, and explicit unknown/loading gate presentation. Replace the unbounded sprint select with active selection plus a small paged history picker; preserve a selected historical header even if it is not on the current history page.

State identity is `(projectId, sprintId, pageCursor, requestGeneration)`. On project switch, cancel pending requests, clear old-project content immediately, reset selected sprint/history/member cursors, and fetch the new summary/default board. Selection changes fetch that board and its draft panel rather than relying on `SelectedSprint` finding a row in all loaded history. Late success **and failure** from a previous generation cannot update the current selection. Missing/deleted selected sprint is an explicit error with a return-to-active action, not a silent switch.

Keep the current 30-second backstop and debounce, but funnel timer, Refresh and SSE invalidation through one in-flight refresh with one pending refresh flag. Do not overlap requests or use `Task.Run` to wrap an async fetch. Use cancellation on disposal. SSE currently supplies only an event kind (`OnServerEvent`), so do not claim project filtering that the payload cannot support; coalesce relevant event kinds and use the same bounded reader. No background refresh of every history page.

Initial load shows loading; initial failure shows error/retry; refresh failure retains last good same-project data with a stale timestamp. Empty history/empty board requires a successful response. Refreshing does not blank the board. History loading has independent failure state so a failed older-page fetch does not erase the active board. Header counts come from server totals, never `visibleHeaders.Count`.

## Adjacent reads: avoid hiding the remaining cost

Sprints also loads queue, build state and draft panels (`Sprints.razor:483`, `:636`, `:677`). Moving the board alone removes the historical N+1 export, but does not make the whole page bounded:

- `QueueEndpoints.cs:54` calls `ReadyAsync(0, null)` across the project, then filters membership. It also loads all Pending rows for blocked-member titles. `IssueStore.ReadyAsync` applies its `limit` after materialization (`IssueStore.cs:2100`); merely passing 8 is not sufficient.
- The first patch should give the Sprints queue read a bounded SQL projection restricted to the active sprint, with exact totals and a small preview (the page currently displays eight entries). Keep dispatch's `ReadyAsync` and claim-order semantics unchanged. Reuse its blocker/priority/FIFO predicate in tests; do not invent new eligibility. Either add an optional bounded queue-preview path or include it in the board endpoint for active selections. Other queue consumers retain their contract.
- Draft panel selection currently follows active/newest rather than the operator's selected sprint (`Sprints.razor:683`, `:695`). Tie it to the returned board id while changing selection plumbing. Do not change draft materialization/approval policy.
- Build-state data is an existing persisted snapshot. Keep its endpoint initially; measure payload size and display bounded previews with explicit totals if large. Do not rebuild sprint planning inside a read endpoint.

These are bounded read changes only. The user has not authorized a new initiative/approval workflow through this performance fix.

## Meaningful qualification

1. SQLite fixtures with 0, 1 and 500 historical sprints; thousands of unrelated issues; one selected sprint. Count executed reader/scalar commands with a typed connection/command decorator. Summary and board command count must stay fixed as history grows; assert no universal issue export and no loop of per-sprint/member reads. Add a modest isolated SQL Server run to qualify generated SQL and query plans; SQLite alone is insufficient.
2. Large selected sprint (for example 2,500 members): page bound enforced server-side; exact total/done counts remain correct; no SQL Server 2,100-parameter violation; page traversal has no duplicates/missing rows in a static fixture. Equal timestamps, missing ids, invalid/cross-selection cursors, and requested oversized limits covered.
3. Completed/Closed terminal counts, container/pr-watch exclusion, Failed blockers, external-to-page blockers/boost, nullable/corrupt metadata, PR/rework/lifecycle fields, and existing `TaskSituation` output match the old board for a representative small sprint. No queue mutation occurs.
4. Active sprint older than first history page, no active sprint, historical selection, deleted selection, empty project, project B while A responds late, and late failures all tested. A history page failure must not clear a good board. Project collision fixtures use the same issue id in two stores.
5. Queue preview ordering and exact counts match the dispatch predicate on fixtures including blocker boost, priority ties, parentless ungroomed work, full role slots and model cooldowns. Do not accidentally describe blocked work as claimable or broaden sprint eligibility.
6. Typed frontend tests for request identity/cancellation/coalescing and successful-empty vs error; rendered/manual fixture check that progress uses whole-sprint totals while cards clearly identify pagination. Preserve the confirmed gate/link fixes.
7. Measure a few serial live GETs only after isolated qualification and deployment authorization. Compare response bytes, p50-like repeated samples and query count with the original audit (small live `/api/state` was ~54 KB and ~365–377 ms). Do not call a few small samples a load test, and do not extrapolate a hard latency promise from them.

Acceptance: opening Sprints no longer requests `/api/state`, loads no skill/agent catalog or full issue history, and performs no query-per-historical-sprint. Response/member/history sizes are bounded; totals remain accurate; project switching cannot display another project's board. Existing Backlog/View consumers and all planning/dispatch policies remain unchanged.
