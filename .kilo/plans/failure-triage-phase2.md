# Failure Triage — Phase 2: The Triage Agent (+ corrections from the Phase 1 pass)

Status: ready to work. Builds on Phase 1 (PR #107 — merge + deploy FIRST, see C3).
Prerequisites read: `.kilo/plans/failure-triage-phase1.md` (phase 1 + non-goals).

## Review of the pass that just landed (what drove the corrections)

**Phase 1 implementation (PR #107):** sound — classifier precedence is sane,
the consumer lifecycle (open → action → outcome) is idempotent via store
guards, project lens correct, CI green. Three execution gaps:

- **C3.** Merge + deploy + live verification were left to the operator. The
  definition of done for Forge work is: merged, deployed, and the UI verified
  against LIVE data (screenshot of the real page with real rows).
- **C4.** reset-strikes on an InProgress task emits no clearance signal (no
  status boundary crossed) — open ledger rows stay un-actioned. The consumer
  should instead resolve "is there an open row for this task" on reset-strikes
  via a metadata-marked event, or the endpoint publishes the signal directly.
- **C5.** The /triage page has never rendered live data — the "we fall short on
  UI" habit. Verify on deploy: real rows, real pills, 50%-width behavior.

**The MVP QA-evidence pass (porthorizon):** the acceptance bar said
"screenshots, no merge without visual proof". What merged:

- task-731 (PR #1033): `test-results/LeaseScreenshots/*.json` — JSON state
  dumps named "Screenshots". Fake evidence. Merged anyway.
- task-738 (PR #1028): a capture tool emitting SVG/ASCII grid dumps — better,
  but it bypasses the Godot client entirely, so it proves NOTHING about
  GridSyncSystem/TileRenderer, which was the whole point of Tiers 0–1.

- **C1 (evidence integrity).** A "screenshot" means a RASTER IMAGE from the
  running Godot client — viewport texture dumped to PNG — proving the client
  render path. JSON/SVG/ASCII state serializations never satisfy the bar.
  Two work fronts: (a) porthorizon: a real capture path (headless Godot run
  with a viewport `save_png` hook, invocable from the QA path) and REDONE tier
  evidence; (b) forge: the reviewer verifies evidence TYPE — a task whose
  acceptance mentions screenshots must attach image files, and a changes-
  requested verdict is owed when "screenshots" are state dumps. (This is the
  reviewer-prompt fix, not a new gate.)
- **C2 (PR hygiene).** PH PR bodies are raw thinking transcripts ("Let me look
  at…" narration). The engineer prompt must state: PR body = what changed +
  evidence links; never the transcript.

## Phase 2 scope — the triage agent (deacon-shaped)

Operator-approved 2026-08-23 ("b. love it"). Per-project opt-in; porthorizon
first. No automatic bug-filing (ledger flags only). No cross-project.

### 1. Role registration (ninth role)

- `AgentType.Triage` + `RoleAgentRegistry` entry: pipeline-side role, no
  territory, no sprint membership. Model resolution flows through the existing
  override stack (`llm/roleModel/<project>/Triage` → `llm.roles` → default);
  default to the cheap model — escalation is phase 3.
- `agents/triage.md` role prompt: the failure taxonomy, the action space, the
  guardrails, and the rule that guidance must cite SPECIFIC failure evidence
  (Reflexion artifact), never rephrase the task.
- /agents page lists it via the pipeline catalog (no new plumbing).

### 2. Trigger — no poller

`FailureTriageConsumer` (owner of `forge.task-failure-signal`) publishes a new
`TriageRequested` event on its own topic when (a) the project's triage flag is
on AND (b) the task is under the daily action cap. New `TriageConsumer`
(own topic, competing-consumer rule) runs the agent. Flag off → no event,
zero behavior change.

### 3. The agent's tools (bounded; every action writes the ledger with actor=triage)

1. `requeue_with_guidance(note, context)` — the operator's manual action,
   with the reorientation written from the failure evidence.
2. `park_for_operator(reason)` — judgment calls stay human; parks loudly.
3. `flag_bug_suspect(signature, evidence)` — ledger flag only; NO issue
   creation (operator constraint).

Never available: merges, code edits, gate changes, closes-for-content,
cross-project reads/writes.

### 4. Guardrails (deterministic, store-enforced — not LLM judgment)

- ≤2 triage actions per task per day (ledger query), then auto-park.
- Same signature + same action twice without success → deterministic park
  (the requeue-burn loop prevention).
- Breaker stays 3-strikes; triage requeues consume rounds deliberately.
- Aging sweep remains the final backstop.
- All actions audited via ledger rows + task metadata (`triageNote`,
  `triageAction` on the task, rendered on TaskDetail like planGate).

### 5. Dashboard

- The /triage footer banner becomes a live toggle bound to the project flag
  (`triage.enabled` in the project record — a new `ProjectOptions`/roles_json
  field with a `PUT /api/projects/{id}/triage` endpoint).
- Ledger rows show actor (`operator` / `triage`); a per-task "triage actions"
  strip on TaskDetail.
- Deploy + live-verify with real data (C3/C5).

### 6. Tests

- Flag off → no TriageRequested published (consumer unit test).
- Cap enforcement + park rule (store-level).
- Each tool action: ledger row with actor=triage + task metadata stamped.
- No writes outside the project store (routing test, same pattern as the
  intake routing tests).
- Suite + e2e green; deploy; PR; merge; live verify.

## Order of work

1. Merge + deploy PR #107; verify /triage live against real failures (C3/C5).
2. C1 porthorizon front: intake a "real engine screenshots" epic (headless
   Godot viewport→PNG capture + redo the tier QA evidence; the merged JSON/SVG
   artifacts are void). C1 forge front: reviewer prompt verifies evidence type.
3. C2 (PR-body prompt hygiene) — one-line prompt change.
4. C4 (reset-strikes clearance edge) — small.
5. Phase 2 items 1–6 above.

## Explicit non-goals (phase 2)

- No model escalation (phase 3: per-task single-shot `llm/taskModel/…` +
  escalate tool + budget).
- No automatic issue creation from bug-suspect flags (phase 4, if directed).
- No cross-project enablement; porthorizon only until the operator says so.
