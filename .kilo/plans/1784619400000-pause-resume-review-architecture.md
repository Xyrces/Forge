# Pause/resume review architecture: warm sessions for dev rework + reviewer re-review

Replace the cold-session rework loop with pause/resume: dev runs end at push (slot released), review triggers immediately on PR-open, and corrections resume the *same* persisted conversations — the dev's and the reviewer's.

## Design ratified with operator (2026-07-30)

- **No nested review, no slot held during review.** Dev run = implement + in-session verify (already shipped, `6a4aa0e`) → push → PR → run ends, slot releases.
- **Event-driven review on PR-open** (the 15-min sweep stays backstop). Review runs while CI runs; verdict and CI arrive together.
- **Merge-authoritative `reviewSha`**: approval at the pushed SHA satisfies the approval-at-head requirement; head movement re-reviews.
- **Dev resume**: rework claims resume the dev's stored session with review notes appended; resume prompt notes the worktree was synced to a new head ("re-read files before editing").
- **Reviewer resume**: re-reviews resume the reviewer's stored session with prior verdict + notes + incremental diff since `reviewSha`; framing: verify each prior finding addressed, flag only NEW blocking issues in the new commits (bounds anchoring/rubber-stamping; breaker at 3 rounds stays as hard backstop).
- **Compaction**: deliberately deferred — the context IS the speedup; reviewer input is diff-scoped already.

## Key discovery (changes the work)

`MafAgentRunner.SerializeSessionAsync` is a stub returning null ("Phase 0 doesn't yet need round-tripping"). No session is ever persisted today — every run starts cold. The deserialize path exists and works. So the load-bearing piece is real session serialization:
- MAF 1.12 exposes `agent.SerializeSessionAsync(session)` (public wrapper over protected `SerializeSessionCoreAsync`) → `JsonElement`. Serialize after every run (success AND failure — partial sessions resume fine).

## Implementation steps

1. **Session persistence in `MafAgentRunner`** (`Agents/MafAgentRunner.cs`):
   - Implement real `SerializeSessionAsync` via `agent.SerializeSessionAsync(session)` → `GetRawText()`.
   - `SessionKey(projectId, taskId, role)` = `session/<projectId|_>/<taskId>/<role>`; persist via `_memory.RememberAsync` on completion + on failure (best-effort, log-and-continue).
   - Resume: when `sessionId` param is null, `_memory.RecallAsync(SessionKey(...))` → latest body → existing `DeserializeSessionAsync` (junk → null → fresh).
   - Effect: every run of the same task+role resumes — dev rework rounds AND plain retries get warm automatically.
2. **Resume honesty in the dispatch prompt** (`RunAgentExecutor`/prompt builder): when rework metadata (`reworkForSha`) exists, append "your branch is synced to PR head `<sha>`; re-read any file before editing it."
3. **Event-driven review trigger**: after PR-open (CommitPushPr success path in `OrchestratorAgent`/`CommitPushPrExecutor`), invoke `ReviewerDispatcher.ProcessWatchedTaskAsync` for the task immediately (fire-and-forget with its own error handling; sweep unchanged as backstop). The trigger and the sweep's review path both: publish a `review started` dashboard event (task, prNumber, sha) for the Events tab, and set `reviewStartedAt` metadata on the task (cleared when the verdict lands) — the dashboard's "reviewing…" pill derives from it. Merge still requires CI green + approval-at-head (unchanged).
4. **Reviewer resume** (`Reviewer/ReviewerDispatcher.cs`): load/persist the reviewer session under the same SessionKey with role=Reviewer; re-review prompt = prior verdict + notes + `git diff <previousReviewSha>..<newHead>` + "verify each prior finding addressed; flag only NEW blocking issues in these commits." First review of a PR = cold session (full diff).
5. **UI — every page updated to visualize pause/resume** (per operator 2026-07-30). Schema-first: `agent_run` gains two columns — `phase` (TEXT NULL: `plan gate` / `implementing` / `verifying n/3` / `reviewing` ) and `resumed_session` (INTEGER/BOOL NULL: true when the run resumed a stored session). Bump `CurrentSchemaVersion` in `Core/IssueStore.cs`, update BOTH DDL paths (SQLite migration chain in `InitializeSchemaSqlite`, SQL Server fresh-create in `InitializeSchemaSqlServer`), run `--check` after. `AgentRunStore.AgentRunRecord` gains `Phase`, `ResumedSession`; heartbeat writes phase; `StartAsync` writes resumed_session.
   - **`/api/agent-runs` (+ `{id}`)**: return `phase` + `resumedSession` on run rows (both the list and detail payloads).
   - **`AgentRoleRow.razor` (`ProgressText`)**: running run shows `verifying 1/3 · 42 msgs · 7 tools` (phase prefix when set, existing counters preserved) and a `resumed` marker when `resumedSession` — the "looks stalled during a 3-min verify" problem disappears.
   - **`Runs.razor`**: live-runs table gains a Phase column; a `resumed` pill on resumed runs. **`RunDetail.razor`**: same pill + phase shown in the header block; the transcript already proves session continuity (turn count carries over), no extra work.
   - **`TaskDetail.razor`** — the biggest gap: review metadata (`reviewSha`/`reviewVerdict`/`reviewRound`/`reviewNotes`, written by `ReviewerDispatcher` today) is **not rendered at all**. Add a Review row block beside the plan-gate audit: verdict pill (`approved`/`changes requested`/`pending`), round badge (`R1`–`R3` — reuse the existing rework-pill style), reviewed SHA (short), notes (pre-wrap, capped like reworkContext). Add an explicit "awaiting review" visual: when status is InProgress/Pending with `prNumber` set and no `reviewVerdict` at the current head → "review pending" pill; when the review dispatcher is actively running (new `review started` event sets `reviewStartedAt` metadata, cleared on verdict) → "reviewing…" pill. Session-resume marker on the task's run history rows (from the same `resumed_session` field).
   - **`/now` (NowFeed + Now page)**: live-run cards include the phase label (from AgentRunStore) so a verifying/reviewing run doesn't read as idle; the "awaiting review" count joins the plain-language attention derivation when a PR waits on review past a threshold (reuses the existing stale-window concept, anchored to `prOpenedAt`).
   - **`/flow` (FlowClassifier + journey view)**: teach the classifier the new transition reasons as first-class categories — `llm-auth`, `pre-push verification failed (round n)`, `review requested (round n)`, `resumed session` — so journey timelines read as narrative instead of generic transitions. Journey nodes for review verdicts already exist via the watch; the event-driven trigger just makes them arrive sooner.
   - **Events tab**: publish a `review started` dashboard event (task, prNumber, sha) from the event-driven trigger and from the sweep's review path, so the stream shows review activity between PR-open and verdict; the in-session verify loop needs no events (run-phase covers it).
   - **Sprints page**: no structural change — member pills already derive from task state/metadata; the rework pill keeps working. Verify after implementation that the new pills (review pending/reviewing) don't crowd the member row; if they do, TaskDetail-only.
   - **Design-system discipline** (operator UI rule): every new visual uses existing classes (`pill`, `pill--success`/`pill--blocked`/`pill--primary`, `meta-text`, `banner--*`) — no inline `style=` for shared visuals; if "review pending/reviewing" needs a new tone, add ONE class to `app.css` (e.g. `pill--info`) and reuse it everywhere.
6. **Tests**:
   - Runner: session persists after run (memory store has the key); second run for same task+role receives the stored JSON (deserialize called / fresh on junk).
   - Rework resume: executor prompt includes the synced-head note when `reworkForSha` set.
   - Reviewer resume: re-review prompt contains prior notes + incremental diff range, not full diff.
   - Fallback: corrupt stored session → fresh run, no throw.
   - `AgentRunStore`: phase + resumed_session round-trip (both providers' DDL via the migration test pattern); `StartAsync` writes resumed_session, heartbeat updates phase.
   - UI DTO mapping: `/api/agent-runs` payload includes phase/resumedSession (endpoint test).
7. **Validation**: full suite green; commit; CI; publish + restart; watch the next real rework round on porthorizon (task-9/12/13/31 are mid-loop — their next rounds resume warm); visually confirm on the dashboard: `/agents` phase label during a verify round, TaskDetail review block on a PR under review, `/flow` journey narrative on a resumed rework round.

## Out of scope (noted for later)

- Compaction of large sessions (revisit only on real context/quota pressure).
- Reviewer ≠ dev model-family independence knob.
- `review startedAt` metadata lifecycle is metadata-only (no new state-machine state — the task lifecycle table is code-owned and PROpen already covers "PR open, awaiting outcome"; do NOT invent new lifecycle states for review-pending).

## Risks

- Session JSON schema drift across MAF upgrades → deserialize failure → fresh session (acceptable, logged).
- Memory-store row size: long runs can exceed 100KB; memory body column is unbounded text in both providers.
- Warm-reviewer anchoring: mitigated by incremental-diff + prior-findings-only framing + 3-round breaker.
- Schema migration (agent_run phase/resumed_session): both DDL paths must move together; `--check` verifies before deploy.
