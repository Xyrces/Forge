# Failure Triage — Phase 1: Ledger + Instrumentation + Dashboard Surface

Status: ready to work. Operator-approved scope 2026-08-23 ("a. good scope… b. love it"),
UI designed in Stitch (project "Forge — Triage & Failure Ledger", screen
`15756219201645f481a0038b6764c874`, Forge Ops Console design system).

Context: the 2026-08-23 exploration (gastown Deacon/Witness/escalation,
FrugalGPT cascade scorer, Reflexion guidance, SRE error budgets, Sentry
fingerprint grouping). This phase is **observability only** — no triage agent,
no model escalation, no automatic actions.

## Why

The operator clears Failed/Blocked cards by hand (requeue clicks) with no
record of what failed, why, or whether the fix held. Before any automation we
need the ledger that answers: what fails, how often, what clears it, and is it
a bug or a gate/prompt problem.

## Work items

### 1. Schema v35 — `failure_triage` table (per-project schema)

Both DDL paths (SQLite post-init migration + SQL Server fresh-create) and a new
`M035FailureTriage` migration class. Columns:

- `id` (identity), `task_id`, `failed_at`, `signature` (normalized error class),
- `classification` (taxonomy below), `error_excerpt` (first ~300 chars),
- `action` (NULL until cleared: `operator-requeue` / `operator-close` /
  `operator-reset-strikes` / `aged-sweep`; phase 2 adds `triage-*` actions),
- `actor` (`operator` for now), `acted_at`,
- `outcome` (`succeeded` / `failed-again` / `pending` / NULL while open),
- `escalated_provider`/`escalated_model` (NULL in phase 1; columns exist so
  phase 3 needs no migration).

`FailureTriageStore` in `Core/` (connection-factory pattern, no I/O).

### 2. Deterministic signature classifier (Core)

`FailureSignatureClassifier.Classify(error, metadata) -> (signature, classification)`
— pure function, unit-tested per class. Taxonomy (from the exploration's
empirical table):

| signature | classification |
|---|---|
| `llm-429-quota`, `llm-529-overload`, `gateway-5xx` | transient-upstream |
| `session-pairing-400`, `rework-fossil`, `merged-tarpit` | state-poison |
| `no-diff-bounce` | no-progress |
| `verification-timeout`, `verification-fail` | verification |
| `plan-gate-territory`, `plan-gate-revisions` | gate-loop |
| `review-changes-loop` | review-loop |
| `breaker-exhausted` (3 strikes) | capability-bound |
| repeated same-signature across ≥3 tasks | code-bug-suspect (derived, not stored) |

### 3. Ledger writer — Talaria consumer

New consumer in `Orchestrator/Consumers/` on its **own topic** (competing-
consumer rule): task transitioned to Failed/Blocked → open a ledger row.
Operator clearance (requeue / close / reset-strikes on a task with an open row)
→ record the action. The task's next dispatch result closes the outcome
(succeeded / failed-again — failed-again re-opens with a new row keyed to the
same signature).

### 4. Endpoints (Dashboard)

- `GET /api/triage/ledger?projectId=` — signature-grouped view: per-signature
  count, classification, last-seen, last task, dominant outcome; plus the
  summary-strip numbers (open failures, distinct signatures 7d, aged-sweep
  clearances 7d, escalations 7d = 0 in phase 1, budget placeholder).
- `GET /api/triage/ledger/{signature}?projectId=` — individual rows (task link,
  when, error excerpt, action, outcome, note).

### 5. The Triage page (Forge.UI)

Per the Stitch design (post-refinement state):

- Nav: **Triage** under OPS (shell nav registry + route).
- Summary strip: 4 compact cards (Open failures / Signatures 7d / Escalations
  1-of-5 budget / Auto-cleared 7d) with small sparklines bottom-right.
- Signature ledger: collapsible single-line group rows (severity dot, mono
  signature, count badge, classification pill, compact right-aligned mono
  relative timestamp "2h ago", dominant-outcome pill). NO per-row sparklines,
  no multi-line stacks.
- Expanded group: per-failure sub-table (task link, title, when, mono error
  excerpt, action, outcome, note icon).
- Right rail → stacks below the ledger under ~960px: "Bug suspects"
  (signatures crossing the ≥3-tasks threshold; disabled "manual review for now"
  ghost button) + "Prompt/gate health" bars (weekly counts for plan-gate
  rejections, no-diff bounces, verification timeouts).
- Footer banner: "Triage agent: off for this project — Phase 1:
  observability only".
- UI rules: Fluxor state, app.css classes for new shared visuals
  (`.sparkline`, classification pill variants), no inline styles, dense at
  50% width.

### 6. Tests

- Classifier: one test per signature class + unknown→`other`.
- Store: open/record-action/close-outcome round-trip (SQLite).
- Consumer: transition event opens a row; operator requeue records the action;
  redispatch success closes outcome; same-signature re-failure re-opens.
- Endpoint: grouping + filtering shape tests (test-app convention).
- Suite green + e2e harness green; deploy; PR; merge.

## Explicit non-goals (phase 1)

- No `triage` agent role, no LLM anywhere in this phase.
- No per-task model escalation (columns are placeholders only).
- No automatic actions; bug suspects are "manual review for now".
- No cross-project aggregation (per-project lens; /now stays as-is).

## Later phases (context, not scope)

- Phase 2: `triage` role (ninth registry role) — event-driven on failure
  transitions; tools: requeue_with_guidance, park_for_operator, flag_bug_suspect;
  per-project opt-in flag; ≤2 actions/task/day; same-signature-same-action
  twice = deterministic park.
- Phase 3: per-task single-shot model escalation (`llm/taskModel/<project>/<task>`,
  consumed once by the dispatch bundle), the agent's escalate tool, per-project
  daily escalation budget.
- Phase 4 (only if directed): bug-suspects promote to real issues; multi-project.
