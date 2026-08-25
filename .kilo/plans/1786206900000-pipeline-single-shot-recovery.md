# Pipeline single-shot recovery: shared leaked-markup nudge + designer contract nudge

## Context (verified live 2026-08-24)

Designer runs 184/185 on epic-11 fizzled: `LlmFailed — "LLM completed without committing a spec
status transition"` (design lane retried every 15m; epic-11 only advanced out-of-band). Root
cause class: all six pipeline agents use single-shot `ChatClientAgent.RunAsync` —
`Orchestrator/DesignerAgent.cs:155`, `Orchestrator/ArtistAgent.cs:148`,
`Agents/GroomerAgent.cs:230,463`, `Agents/IntakeAgent.cs:303`, `Agents/TriageAgent.cs:130`,
`Agents/ProductAgent.cs:220` — which bypasses `MafAgentRunner`, the only place the leaked-markup
nudge exists (`Agents/MafAgentRunner.cs:434-453`, detector at :1325-1330; 34 historical
`]​<]minimax[>` occurrences in agent.log). The designer's seeded model is minimax M3 — the
leak-prone one. Groomer/intake/triage work today but are one unlucky leak away from the same
silent retry loop. Compounding observability gap: `DesignerRun` (Core/DesignerRunStore.cs:202)
stores no response text, so a fizzle records nothing about what the model actually said.

Operator decision (2026-08-24): **shared wrapper, all six sites** (not designer-only, not a
MafAgentRunner migration).

## Design

### Shared wrapper (`Agents/PipelineAgentRunner.cs`, new)
Single-shot `ChatClientAgent.RunAsync` with conversation-maintained continuation:
- Loop shape (mirrors MafAgentRunner): `conversation = [prompt]`; per round
  `response = await agent.RunAsync(conversation)`; `conversation.AddRange(response.Messages)`;
  on nudge: `conversation.Add(nudge)` and continue. **One shared continuation counter, max 3**
  across both nudge kinds.
- Leaked-markup detection: extract the detector (`]<]minimax[>` family) from
  `MafAgentRunner.cs:1325-1330` into a shared internal helper both the wrapper and MafAgentRunner
  use — no duplicated pattern lists. Nudge text: extract `LeakedToolCallContinuationPrompt` to
  the same shared home (single source).
- Optional completion contract: caller passes `requiredToolName` + a contract-nudge prompt; when
  a round completes with NO leaked markup but also no `FunctionResultContent` from that tool,
  fire the contract nudge ("you must call <tool> to finish"). Only the designer passes one today
  (`db_set_spec_status`: Designed/Approved/NeedsRevision). Other sites pass none (their
  no-tool-call outcomes are legitimate today); wiring groomer/product contracts is a follow-up.
- Returns the final `AgentRunResponse` + a record of nudges fired (for logging).

### Site migrations (mechanical)
All six call sites route through the wrapper. Designer keeps its post-run source-of-truth check
(re-fetch spec status) exactly as-is — the wrapper's contract nudge is only the recovery; the
committed status remains the verdict.

### Fizzle observability (no schema change)
When the wrapper exhausts its continuation budget, the caller's failure path includes a
final-text excerpt (~500 chars, first/last) in the existing `error` field + a warning log line —
e.g. DesignerAgent's `"llm did not call db_set_spec_status"` gains
`"final text: <excerpt>"`. Applies to every migrated site's failure path.

## Tasks (ordered)

1. Extract the leaked-markup detector + nudge prompt from MafAgentRunner into a shared internal
   helper (Agents/); MafAgentRunner switches to it (no behavior change — pin with its existing
   tests).
2. `PipelineAgentRunner` wrapper: conversation loop, leak nudge, optional contract nudge,
   shared counter (max 3), nudge-audit return. Unit tests with `StubbedChatClientFactory`:
   leak-then-recover, contract-nudge-fires, budget-exhausted-fails, no-nudge happy path,
   counter shared across both nudge kinds.
3. Migrate DesignerAgent (contract = `db_set_spec_status`); fizzle error gains the final-text
   excerpt. Test: designer run recovers from a leaked status call; fizzle error contains excerpt.
4. Migrate Artist, Groomer (both paths), Intake, Triage, Product (no contract nudge).
   Per-site smoke tests where fakes exist.
5. Validate: full suite green, `TreatWarningsAsErrors` clean, `--check` passes.
6. PR → deploy release 53 (operator-authorized; confirm no active runs; no gate holds needed —
   no model-config window). Rollback = repoint to release 52.

## Validation / live proof

- Live proof awaits a natural design-lane spec (none in ReadyForDesign right now — epic-11
  already advanced): the next spec's designer run either succeeds or, on a leak, recovers with
  visible nudge log lines; a true fizzle now carries the final-text excerpt in
  `/api/designer/runs`.
- V7 (escalation e2e) remains pending a natural capability-bound failure — unchanged, do not force.

## Risks

- Nudge prompts change model behavior on healthy runs: counter-bounded (3) and only fires on
  detected leak / missing contract — healthy runs are untouched.
- Intake is conversational (dashboard chat history): the wrapper must accept caller-supplied
  history, not just a single prompt string — design the entry point for both shapes.
- Contract nudge on a spec the model legitimately wants to leave alone: the nudge text must
  include `NeedsRevision` as a valid exit (it does — the tool's enum).

## Out of scope

- Contract nudges for groomer/product/artist (their no-call outcomes are legitimate today;
  wire later if a fizzle is observed).
- Migrating pipeline agents onto MafAgentRunner (sessions/heartbeats/run registry).
- DesignerRun schema changes (excerpt rides the existing error field).
