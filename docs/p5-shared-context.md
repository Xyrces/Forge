# P5 — Native SharedContext

Status: design. Goal: replace the role of Headroom's `SharedContext` (https://headroom-docs.vercel.app/docs/shared-context) and `with_memory` (https://headroom-docs.vercel.app/docs/memory) using the orchestrator's own primitives, not a Python sidecar. **No Headroom dependency for this layer.**

## Why native, not Headroom

Headroom's SharedContext is a Python library + TypeScript SDK. To use it from .NET we'd need one of:
- (a) a Python sidecar with an HTTP seam, OR
- (b) port the tree-sitter / ModernBERT / SmartCrusher pipeline to C#, OR
- (c) call Headroom's `/v1/compress` HTTP endpoint per request.

We already explored (a) + (b) + (c) in the Headroom work and chose sidecar for compression. The same approach for SharedContext would mean yet another Python process the operator runs. The benefits of Headroom's pipeline (80% compression on agent handoffs, CCR tool) are real but **duplicative of what we can do natively** for our specific handoff pattern.

Our actual handoff pattern is: `Designer → Artist → Groomer → Engineering`. The state that flows between them is:

1. **The spec body** — already a row in the `spec` table; a `body` field that's already passed by id reference.
2. **Design artifacts** — already a `design_artifact` table; the next agent gets them via `db_get_existing_design_artifacts` (the Designer's AIFunction).
3. **Memory keys** — already a `memory` table; `playbook/repo`, `playbook/snapshot`, `playbook/skills/<role>`, `vision/master`. The existing `MemoryStore` covers this.
4. **Tool output** — the LLM response. Compress-on-write at the LLM-side, decompress-on-read via CCR. Headroom's compression pipeline would help here; the operator's `headroom` proxy can stay opt-in for this layer.

**The bottleneck isn't storage or retrieval — it's that we don't *index* the handoff content well.** The Designer writes a long spec body. The Artist's prompt includes the full spec body inline. The Engineer reads the same spec body again. We re-send the same content multiple times because nothing tells the orchestrator "use the spec's body as an index; only fetch artifacts on demand."

## Goals (small, achievable in 2-3 weeks)

1. **Spec body becomes an index, not a payload.** Today the spec body is a markdown blob with full text. Replace with a structured header (title, acceptance criteria, module list) + artifact references. The next agent's prompt gets the header; artifact bodies are pulled on demand via a `read_artifact` tool.
2. **`read_artifact` AIFunction.** New MAF tool that returns the full body of a `design_artifact` or `spec_version` by id. Each agent has it; agents use it instead of the LLM re-summarizing from the spec body.
3. **Auto-extracted memories on commit.** When a Designer commits a design or an Engineer commits code that contains "design decisions" (per `CodingStyleRules`), write a `DESIGN_DECISION` memory automatically. Next agent has those decisions in their prompt.
4. **Persistent context lineage (lightweight).** A small `context_handoff` table that records: agent A produced spec X; agent B read artifact Y while acting on spec X; agent C read artifacts Y, Z. Cheap audit trail; useful for debugging the closed loop.

## What we explicitly do NOT do

- No tree-sitter / ModernBERT / SmartCrusher port. The auto-extraction is rule-based + LLM-asked (one-shot summarization) — Headroom's pipeline is the better answer if compression becomes a problem, and the operator can flip the `headroom` proxy on independently.
- No python sidecar. If we ever need Headroom's full pipeline, the existing `headroom` proxy already serves the chat-completions path; the spec body + design artifacts can pass through it the same way.
- No per-token-budget system. The operator has not asked for that. A future P5 task if needed.

## What we already have (the leverage)

- **`SpecStore`** with `body` (markdown) + `CurrentVersion` (int). 7-table schema (v7→v11).
- **`DesignArtifactStore`** with `kind` (mesh | texture | animation | rig) + `bodyKind` (glb | png | mp4) + `body` (path) + `references_json`. The body is the path, not the file content — the spec already references by id.
- **`MemoryStore`** with `key` (string primary key) + `value` (text) + `author` + `category` (FACT | PREFERENCE | DECISION | etc.). Persistent across the orchestrator's lifetime.
- **`SkillBootstrap`** — `playbook/repo`, `playbook/snapshot`, `playbook/skills/<role>` per role. Operator-overridable.
- **MAF ChatClientAgent** with `AIFunctionFactory.Create` for tools. Adding `read_artifact` is ~20 lines.
- **`CostTracker`** — already aggregates per-call token usage. We'll add a `totalMemoryHits` counter so the operator can see how often the LLM called `read_artifact` vs. got it inline.

## Concrete design (per goal)

### Goal 1: spec body becomes an index

**Today:** the Designer's output goes into the spec's `body` field (long markdown). Each subsequent agent's prompt is built by `MafAgentRunner` to include the spec body verbatim.

**Proposed:** the spec body is split into two regions:
- `body` (header, "index" part): the spec metadata, acceptance criteria, the `Touches:` module list, the artifact references. Always inlined into prompts.
- `bodyArtifacts` (the bodies): the actual prose explanations, code examples, edge-case descriptions. Stored in `spec_version.body` as before, but **never** inlined. Only available via `read_artifact`.

Concretely: the `MafAgentRunner` includes a slim header (~200 tokens) in the system prompt; the LLM sees `[read_artifact <id>]` markers for the bodies. The LLM calls `read_artifact` for the ones it needs.

This is roughly equivalent to the way Claude's skills work: a "skill" is a long-form doc; the system prompt has a one-line summary; the LLM reads the full doc on demand.

**Where this lives in code:**
- `MafAgentRunner.BuildPromptAsync` (or whatever the current method is called) — split the prompt construction.
- The split point: anything in the spec body that matches a `<!-- artifact:<id> -->` marker is treated as a body reference. The rest is treated as header.
- The Designer agent is responsible for emitting those markers. Add a guideline to the Designer's system prompt + a validation check in `DesignHygieneChecker` (one of the existing 10 rules).

**Effort estimate:** 1-2 days. Mostly a prompt-construction refactor + one rule in the hygiene checker.

### Goal 2: `read_artifact` AIFunction

**Today:** the Designer has `db_save_design_artifact` and `db_get_existing_design_artifacts` (AIFunctions). The next agent (Artist, Groomer) sees artifact references in the spec body but has no tool to read them.

**Proposed:** add a generic `read_artifact` AIFunction to the MAF agent loop. Available on all roles (Designer, Artist, Groomer, CoreDev, ClientDev, QA, Reviewer). Reads from any of: `design_artifact`, `spec_version`, `art_output` (mesh), `skill_<role>` (the playbook content).

```csharp
[Description("Read a full artifact body by id. Use this when the spec index references an artifact and you need the full content. Returns null if the id doesn't exist.")]
public async Task<string?> ReadArtifact(
    [Description("Artifact id. Recognized: design-xxx (design artifacts), spec-xxx (spec body for a version), art-xxx (art outputs).")]
    string artifactId)
{
    if (artifactId.StartsWith("design-"))
        return (await _designArtifacts.GetAsync(artifactId))?.Body;
    if (artifactId.StartsWith("spec-"))
    {
        var v = await _specs.GetAsync(artifactId);
        return v?.Body;
    }
    if (artifactId.StartsWith("art-"))
        return (await _artOutputs.GetAsync(artifactId))?.Body;
    return null;
}
```

**Where this lives:** `AgentTools/ArtifactReadTool.cs`. Added to the MAF ChatClientAgent's `tools` list in `MafAgentRunner`.

**Effort estimate:** half a day. Mostly plumbing the storage access.

### Goal 3: auto-extracted memories on commit

**Today:** the operator manually calls `POST /api/memory` to add project-level insights.

**Proposed:** the existing `CommitPushPrExecutor` (in P3) already runs after a task's git commit. Add a step after the commit: ask the LLM, in a single short call, "Given this commit diff, are there any design decisions worth remembering for future tasks? Format: a single line `<memory><key>...</key><value>...</value></memory>` or 'no'." Store the result via `MemoryStore.AddAsync`.

**Why this is fine:** the existing `with_memory` flow has the LLM extract facts inline (per the Headroom docs). We're not doing inline; we're doing a separate small call at commit time. The cost is one extra small LLM call per task. The benefit is that *decisions* get remembered automatically, without the operator having to maintain `/api/memory` by hand.

**Memory categories we'll auto-classify into:** `DECISION` (a "we chose X over Y because Z"), `INSIGHT` ("the orchestrator behaves this way"), `FACT` (the project structure). Skip `PREFERENCE` + `CONTEXT` — those should remain operator-curated.

**Where this lives:** a new `MemoryExtractor` service. `CommitPushPrExecutor` calls it after the commit. The extractor uses the kilo gateway (already wired) with a small fast model (M3 is fine; even a smaller model would work).

**Effort estimate:** 1 day. ~150 lines of new code. One new endpoint `/api/memory/extractions/{taskId}` for the dashboard to show what was extracted.

### Goal 4: context_handoff lineage

**Today:** the `dispatch_checkpoint` column records the per-stage progress of a single task's engineering dispatch. We don't track cross-task lineage (Designer of task A → Engineer of task B that consumed task A's artifact).

**Proposed:** a small `context_handoff` table:
```sql
CREATE TABLE context_handoff (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id         TEXT NOT NULL,
    from_role       TEXT NOT NULL,    -- 'designer' | 'artist' | 'groomer' | 'core_dev' | ...
    to_role         TEXT NOT NULL,
    artifact_id     TEXT,            -- what was passed
    consumed        INTEGER NOT NULL DEFAULT 0,  -- did the next agent actually read it?
    created_at      TEXT NOT NULL
);
```

When `read_artifact` is called, log a `context_handoff` row: `(task_id, from_role=null (spec is the source), to_role=current_role, artifact_id, consumed=1)`.

**Why this is useful:** debugging closed loops. "The Artist didn't see the wireframe." → look at context_handoff and see that the Artist's read_artifact call didn't happen for the wireframe id. Without this, the only signal is "the spec said X; the artist did Y" with no causal link.

**Effort estimate:** 1 day. Mostly a new table + a single new method on the artifact-read tool.

## Cost / benefit vs Headroom SharedContext

| | Headroom SharedContext | Native (this design) |
|---|---|---|
| **Token savings on handoffs** | ~80% (Headroom's claim) | ~30-50% (spec body has ~30% artifact references; LLM calls `read_artifact` only for the few it needs) |
| **Implementation cost** | High (Python sidecar + HTTP seam OR C# port) | Medium (one new tool + one auto-extract call) |
| **Operational cost** | New process to deploy + monitor | Zero (existing services) |
| **Dependency** | Headroom Python (separate from Headroom proxy) | None |
| **Observability** | Per-entry `savingsPercent` + `transforms[]` | `read_artifact` call counts (already in CostTracker pattern) |
| **When to revisit** | If handoff tokens grow large + LLM is slow at cross-task context | Right now. If Headroom's compression becomes necessary, we add the proxy for that *specific layer* without rewriting the handoff logic |

## Implementation order

1. **`read_artifact` tool** (Goal 2). Half day. Test: write a unit test that exercises the lookup; integration test in e2e harness.
2. **Spec body split** (Goal 1). 1-2 days. Touches MafAgentRunner, DesignHygieneChecker, DesignerAgent. Risk: changes the Designer's behavior; need a live verification.
3. **`context_handoff` lineage** (Goal 4). 1 day. Mostly a new table + a single instrumented call. Low risk.
4. **Auto-extract memories on commit** (Goal 3). 1 day. New service + new endpoint. Modest risk.

Total: 4-5 days. **Re-measure Headroom after** this design ships — the operator's flagged concern (token mode + cache misses on long contexts) is partly solved by the spec body split (shorter prompts = smaller cache-busting surface).

## When to reconsider Headroom

- If the spec body is still > 4K tokens after the split, AND the LLM cache hit rate is poor, AND the auto-extraction doesn't capture enough — that's when Headroom's compression layer on top becomes worth it. The operator's `headroom` proxy is already opt-in via the same `headroom.enabled` flag, so it's a one-line config change to enable.
- If we need **stronger** summarization (the spec body's "what does this mean" → "what should I do"), Headroom's Kompress + CCR tool is the right answer. We'd add the SharedContext sidecar at that point — small, focused, complementary to the native pieces in this design.

## Open questions for the operator

1. **Spec body split trigger:** on-commit post-process (less Designer risk) vs on-write agent emits artifacts + markers (cleaner result) vs both (Designer is unconstrained, structure is enforced). I'd lean both.
2. **read_artifact per-call limit:** large spec bodies could push the LLM to read too much. Cap at 10K tokens per call? Configurable via appsettings?
3. **Auto-extract on every commit, or only on human-flagged commits?** I lean every commit (low cost, opt-out by config). But the operator may prefer to gate on the task's "complexity" or "duration."