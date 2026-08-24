# Failure triage phase 3 — per-role escalation models + triage escalate action

Prerequisite: deploy of f515cc5 + live-verify (`.kilo/plans/1786201235000-deploy-f515cc5-live-verify.md`).
Phase 2 must be live with `$triage` enabled on porthorizon.

## Operator decisions (2026-08-23)

1. **Agent-driven only.** Escalation is a 4th `TriageTools` action the triage agent chooses at
   triage time with an evidence-cited note (same Reflexion rule as `requeue_with_guidance`). No
   deterministic auto-escalation; no operator-only verb.
2. **No count budgets.** Frequent escalation is a signal to fix why tasks keep failing (gates,
   territory, prompts), not something to ration. $$ budgets are a possible future concern, out of
   scope. The phase-1 `EscalationBudget = 5` placeholder in `Dashboard/TriageEndpoints.cs:20` is
   REMOVED, not wired.
3. **Concurrency bound instead:** cap of **1 escalated run per (project, role)**, implemented
   through the EXISTING `SlotTable` (project, role) semaphore pattern — no new concurrency
   primitive. Zero-timeout acquire at dispatch; full → task stays Pending, loop retries next
   cycle. The escalated run does **NOT** also occupy its normal role slot: slots exist to bound
   per-model concurrency (rate limits), and the escalated run rides a DIFFERENT model with its
   own limit pool. The existing per-provider request semaphore (`llm.maxConcurrentRequests`)
   stays the lowest layer. Design for the future without building it: per-project-per-model
   limits where a run takes the MINIMUM of every applicable limit — shape the slot key so a
   (project, model) bucket dimension is a natural extension later.
4. **The triage agent does NOT pick the target model.** Each role gets an optional, explicitly
   configured **escalation model**. The tool is `escalate_model(taskId, note)` — no model param.
5. **Role-model inheritance is removed.** Every `AgentType` resolves independently via
   override → `llm.roles` → provider default (designer/groomer/artist no longer inherit coredev).
   Landed with a **one-time seed** so live behavior doesn't silently change (task 6).
6. **Explicit-only escalation:** the tool validates the task's role HAS an escalation model
   configured; unset → error string to the agent ("no escalation model configured for <role> — set
   one on /agents"), nothing written, no action spent.
7. Escalation **counts toward the ≤2 triage actions/task/day** cap (it is a triage action).

## Mechanics

### Per-role escalation model (new override tier)
- `Agents/RoleModelOverrides.cs`: add a parallel escalation map. DB keys
  `llm/roleEscalationModel/<projectId>/<AgentType>` with global fallback
  `llm/roleEscalationModel/<AgentType>` (project-scoped must never leak cross-project — operator
  rule 2026-07-30). Live in-memory snapshot + sync reads, same as role models (no restart).
- Resolution: project escalation override → global escalation override →
  `llm.roles.<AgentType>.escalationModel` (new optional config field, providerName+model
  symmetric with the role entry) → **unset**.
- /agents page: each role card shows model AND escalation model, both editable
  (`PUT/DELETE /api/agents/roles/{name}/escalation-model`). Role catalog unchanged
  (`RoleAgentRegistry.All()` + Pipeline + AllSlotRoles).

### Single-shot task escalation marker
- Memory key `llm/taskModel/<projectId>/<taskId>` in the primary MemoryStore (same store as role
  overrides). Value: escalated-at, actor=triage, note.
- Consumed ONCE by the dispatch path: when `OrchestratorAgent` builds a run for a task with a
  marker, it resolves the task's role (`RoleAgentRegistry.FromTaskType`) escalation model instead
  of the normal chain, DELETES the key (no refund on run failure), and stamps run metadata
  (`modelEscalated=true`, from→to models) + the run registry's model label.
- Cooldown key (`ResolveModelKey`, `Core/ModelRateLimitTracker`) must be the ESCALATED
  provider+model for that run — a 429 on the premium model must not cool the normal one.
- Marker targets the task's next DEV run. Watch-lane roles (reviewer/qa) are out of scope for
  escalation v1 even though the override mechanism is uniform across AgentTypes.

### Concurrency cap (1 escalated run per project+role; replaces the normal slot for that run)
- Rationale: slots are model-concurrency limiters (rate-limit protection). A run on the
  escalation model draws on a different model's budget, so it must not consume the normal model's
  slot.
- `Orchestrator/Slots/SlotTable.cs`: escalated runs ride the SAME (project, role) semaphore
  machinery — an escalation dimension on the existing pool key (e.g. pool `escalated:<role>` per
  project, max 1), not a bespoke structure. At dispatch, a task carrying the escalation marker
  acquires ONLY its (project, escalated-role) slot (zero-timeout; full → skip this cycle, task
  stays Pending, same as a full role pool) and never touches the normal role pool. Released at
  run end (success or failure).
- Key the escalation pool so a future (project, model) bucket layer composes: the pool identity
  derives from (project, role, escalation-model-ref) internally, making "min of all applicable
  limits" a data change later rather than a rework. Do NOT build per-model limits or
  min-resolution today — out of scope.

### Triage tool + audit
- `Agents/TriageTools.cs`: `EscalateModelAsync(taskId, signature, note, ct)` mirroring
  `RequeueWithGuidanceAsync`'s guards: task exists, Failed|Blocked, open ledger row, row not
  already actioned; PLUS the role-has-escalation-model check (reads the escalation override
  resolution for the task's project+role). Ledger action `escalate_model`, actor=triage, outcome
  pending (resolved by the escalated run like a requeue). Metadata: `triageAction="escalate"`,
  `triageNote`, `triageActionAt` (TaskDetail strip renders unchanged).
- Tool errors return strings to the agent; guardrail park/daily-cap logic is unchanged (escalate
  spends one of the 2/day/task actions).
- `agents/triage.md`: document `escalate_model` + when to use it — evidence indicates a
  capability-bound failure (repeated `plan-llm-review` rejections of sound plans, complex
  multi-file refactors collapsing) and NOT territory/gate-loop/process failures (those get
  requeue/park/flag). The prompt does NOT enumerate models (the agent never picks one).

### /triage summary strip reshape
- `Dashboard/TriageEndpoints.cs`: drop the `EscalationBudget` placeholder; derive `escalations7d`
  from the ledger (count of `escalate_model` actions, rolling 7d); optionally surface
  `escalatedInFlight` per (project, role) from the slot table. UI strip follows (app.css classes).

### Inheritance removal
- Find the inheritance in `Configuration` `LlmConfig.ResolveEffective` (designer/groomer/artist →
  coredev) and cut it: every `AgentType` resolves override → `llm.roles.<AgentType>` → provider
  default. AGENTS.md "pipeline model semantics" paragraph updated (intake + triage own; NOTHING
  inherits anymore).

## Tasks (ordered)

1. Escalation override tier in `RoleModelOverrides` + `LlmConfig` (`escalationModel` field) +
   resolution + `PUT/DELETE /api/agents/roles/{name}/escalation-model`.
2. Inheritance removal in `ResolveEffective` (+ tests pinning per-role independence).
3. `llm/taskModel/<project>/<task>` marker: write (tool), single-shot consume at dispatch
   (resolve escalation model, delete key, run metadata, cooldown key), tests.
4. SlotTable escalation dimension (1 per (project, role), reusing the existing pool machinery;
   escalated runs acquire ONLY the escalation slot, never the normal role slot; pool key shaped
   for a future (project, model) bucket layer) + dispatch acquire/release + tests.
5. `TriageTools.EscalateModelAsync` + guards + ledger action + metadata; `TriageConsumer` guardrail
   accounting counts escalate toward the daily cap; tests.
6. **One-time seed (deploy-time, not code):** for each live project, `PUT` explicit model
   overrides for designer/groomer/artist equal to the CURRENT effective coredev resolution
   (records what they inherit today) — executed via the existing
   `PUT /api/agents/roles/{name}/model` API after deploy, before any operator changes.
7. /agents UI: escalation-model display + edit per role; /triage strip reshape.
8. `agents/triage.md` prompt update; AGENTS.md model-semantics paragraph.

## Validation

- New tests: escalation override resolution (project→global→config→unset); inheritance cut;
  marker write/consume-once/no-refund; cooldown keys on escalated model; concurrency cap (second
  escalated task for the same (project, role) waits; a different role in the same project
  dispatches; the escalated run never touches the normal role pool; failure releases); tool
  guards (no escalation model → error, already-actioned → error, daily cap).
- Full suite green; main project clean under `TreatWarningsAsErrors`; `--check` passes.
- Live verify (post-deploy, operator-authorized): set an escalation model for coredev on /agents;
  on porthorizon's next failure crossing, observe the triage agent's `escalate_model` action on
  the ledger, the next run on the escalation model (run registry label + `modelEscalated`
  metadata), marker gone after dispatch, TaskDetail strip shows the action; a second escalated
  task for the same role waits while the first is in flight (slot pill visible on /agents).

## Failure modes / risks

- Escalation model 429s: per-model cooldown isolates it (task 3); the run fails and re-enters
  triage — bounded by 2/day/task and the 3-strike breaker.
- Marker consumed then run infra-fails: no refund by design (single-shot); the triage agent can
  re-escalate (spends another action).
- Seed step forgotten at deploy: designer/groomer/artist fall to provider default — loud (model
  label on /agents + run registry), not silent data corruption; recover by running task 6 late.
- Concurrency cap full while a long escalated run executes: another escalated task for the SAME
  (project, role) waits in Pending; other roles/projects dispatch normally, and the project's
  normal role pools are unaffected by escalated runs — intended.
- Normal-pool starvation is NOT a risk: escalated runs free their normal role slot to non-escalated
  work, so escalation can only increase a project's throughput, never decrease it.

## Out of scope

- Count/$$ escalation budgets (operator direction: fix why tasks fail instead).
- Per-project-per-model slot limits with min-of-applicable resolution (future; today's pool key
  is shaped so it's a data change, not a rework).
- Deterministic auto-escalation; operator-only escalation verbs.
- Escalating watch-lane roles' runs (reviewer/qa) via task markers.
- Phase 4 (bug-suspects → real issues) — only if directed.
