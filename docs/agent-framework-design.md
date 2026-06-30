
# Microsoft Agent Framework migration

Status: P0 closed 2026-06-30. Goal: replace the kilo-based runtime in PortHorizon.Agents with Microsoft Agent Framework (MAF) as the agent runtime, while keeping the SQLite-backed issue/agent/skill/sprint store and the Kestrel dashboard.

**P0 outcome (2026-06-30):** the MAF runner is the only agent runtime. The `AcpClient`/`AcpProcessManager`/`AcpSession`/`AcpProtocol` files and their integration tests are deleted. `IAgentRunner` (Microsoft.Agents.AI 1.11.1) is wired into `OrchestratorAgent`; `MafAgentRunner` wraps `ChatClientAgent` with the role's `.kilo/agents/<role>.md` frontmatter as `instructions:`. 89/89 tests pass. P0.5+ phases (real LLM providers, git/PR tools, intake via HarnessAgent) proceed per the table below.

## Why we are doing this

The P0-P1 orchestrator drove agents by shelling out to `kilo serve` and talking its bespoke HTTP+JSON protocol. Two failure modes:

1. **Wire fragility.** kilo's POST `/session/<id>/message` does not return until the agent finishes. We side-stepped this with a parallel poll-and-cancel harness in `AcpClient.PromptAsync`. The fix worked but was a workaround for a quirk of a tool that is not our problem to fix.
2. **Provider coupling.** We were locked into `kilo acp`'s tool protocol, prompt shapes, and session IDs. Switching to a different LLM provider meant writing a parallel `AcpClient` for each one.

MAF removes both. It is the direct successor to both Semantic Kernel and AutoGen, designed by the same Microsoft teams to consolidate the two. It is open-source (.NET + Python), production-graded (1.x at dotnet 1.11.1; 11.8k stars), and has explicit migration documentation for both SK and AutoGen.

It also offers primitives we currently lack and would otherwise have to build:

- `AIAgent` + `AgentSession` with explicit serialize/deserialize for restart-safety.
- `Workflows` (graph-based fan-out / fan-in) that replace our hand-rolled `DispatchCycleAsync`.
- `Microsoft.Agents.AI.DurableTask` â€” orchestrations backed by Durable Functions; restarts do not lose in-flight work; external events replace PR polling.
- `AgentSkillsSource` / `AgentSkillsProvider` â€” a uniform abstraction for filesystem-, DB-, or class-defined skills (see `docs/decisions/0021-agent-skills-design.md`).
- Provider portability via `Microsoft.Extensions.AI.IChatClient`: OpenAI, Azure OpenAI, Microsoft Foundry, Anthropic, GitHub Copilot SDK, Ollama.



## Target architecture

Three layers, all in-process at first, with a future option to run the orchestrator on Durable Task.

```
PortHorizon.Agents.exe
â”œâ”€â”€ PortHorizon.Agents.Core         SQLite + IssueStore/AgentStore/SkillStore/SprintStore (UNCHANGED)
â”œâ”€â”€ PortHorizon.Agents.Orchestrator  Dispatches issues to MAF agents
â”œâ”€â”€ PortHorizon.Agents.Agents        SqliteAgentSkillsSource, RoleAgentDefinitions
â”œâ”€â”€ PortHorizon.Agents.Tools        Git tools, PR tools, shell tools (AIFunctions)
â”œâ”€â”€ PortHorizon.Agents.Dashboard     Kestrel + dashboard (UNCHANGED)
â””â”€â”€ Microsoft.Agents.AI.*           MAF runtime packages (NuGet prerelease for now)
    â”œâ”€â”€ Microsoft.Agents.AI              Core AIAgent + IChatClient + AgentSession
    â”œâ”€â”€ Microsoft.Agents.AI.Harness      HarnessAgent + HarnessAgentOptions + todo/memory providers
    â”œâ”€â”€ Microsoft.Agents.AI.Workflows    WorkflowBuilder, fan-out/fan-in, in-process runtime
    â”œâ”€â”€ Microsoft.Agents.AI.Tools.Shell  LocalShellExecutor / DockerShellExecutor
    â”œâ”€â”€ Microsoft.Agents.AI.Foundry      Or OpenAI / Anthropic / GitHubCopilot, depending on provider choice
    â””â”€â”€ (future) Microsoft.Agents.AI.DurableTask
```

The orchestrator becomes:

1. Read active sprint + its issues from SQLite.
2. Build a `WorkflowBuilder`. For each issue, construct a `ChatClientAgent` (or `HarnessAgent`) with: instructions, role scope, `SqliteAgentSkillsSource` (per-issue scope), and a `FileMemoryProvider` rooted at the issue worktree.
3. Use `AddFanOutEdge` so issues run in parallel up to a configured max-concurrent.
4. Each agent's executor:
   a. Acquires a worktree (via `git_worktree_add` AIFunction).
   b. Runs the LLM (call returned by the chat client).
   c. After the LLM signals "done" (terminal tool call or done message), commits and pushes (`git_commit`, `git_push` AIFunctions).
   d. Opens a PR (`open_pull_request` AIFunction) via Octokit.
   e. **Yields control** by waiting for an external event (`WaitForExternalEvent("PR_MERGED")` on DurableTask; in-process equivalent: an in-memory `Channel<PRMergedEvent>` polled by an executor).
5. On "PR merged," the executor marks the issue Completed and prunes the worktree.

The operator inbox is no longer a string-prepend: operator messages are injected as `AgentRunOptions.AdditionalMessages` on the next `RunAsync` call, or as `RaiseEvent` against a Durable orchestration.

### Models and providers

MAF standardizes on `IChatClient` from `Microsoft.Extensions.AI`. We pick the provider per environment:

- **Dev**: GitHub Copilot SDK (`Microsoft.Agents.AI.GitHub.Copilot`) â€” no Azure dependency, mirrors the current "use kilo credentials" model.
- **CI/prod**: Microsoft Foundry (`Microsoft.Agents.AI.Foundry`) with a specific deployment.
- **Optional**: Anthropic via `Microsoft.Agents.AI.Anthropic` if we need it.

The provider selection lives in IChatClient registration. Everything above it is provider-agnostic.

Note on Microsoft.Agents.AI.GitHub.Copilot: if we pick this for dev, it is *not* a drop-in replacement for kilo credentials. It depends on a separate NuGet package, the GitHub.Copilot SDK, which has its own runtime, CopilotClient.StartAsync() lifecycle, and license posture. Before committing to it, validate that the SDK runs headless on this host (no interactive login flow) and that GitHubCopilotAgent round-trips through gent.SerializeSessionAsync correctly. It also folds multi-modal DataContent into a temp-dir attachments path, which is a wire-shape difference from MAF's typed content - budget for at least one bug fix in this area.



### What stays vs. what changes

| Capability | Current (kilo) | After (MAF) |
|---|---|---|
| Agent runtime | `kilo serve` subprocess, custom HTTP/JSON | `ChatClientAgent` (or `HarnessAgent`) in-process |
| Agent process model | One session per task, polled for stability | `AgentSession` per task; framework handles finalization |
| Tool execution | Agent calls `bash`/`edit`/`read` via kilo's tool protocol | `AIFunction` delegates attached to agent; framework invokes them directly |
| Skills storage | SQLite `skill` table, populated by UI | Same SQLite `skill` table, exposed via `SqliteAgentSkillsSource` |
| Skill loading | None (skills are metadata only today) | `AgentSkillsProvider` reads skills, exposes `load_skill` / `read_skill_resource` / `run_skill_script` to model |
| Sprint dispatch | ReadyAsync filtered by sprint; one process per task | WorkflowBuilder per sprint; issue.sprint_id join + in-progress cap enforced by the claim path. Engineering claims return null when the sprint's in-progress count has hit sprint.in_progress_cap. |
| Operator inbox | Prepend string to next prompt | `MessageInjectingChatClient` (per `ChatClientAgent`, in-process) OR `RaiseEvent` against a Durable orchestration (DurableTask). Two different semantics; pick per phase. |
| PR creation | `Octokit` directly via `GitHubService` | `open_pull_request` `AIFunction` wrapping the same `Octokit` |
| PR merge watching | 30s polling by `PRWatcher` | External event raised by a GitHub App webhook (DurableTask) or in-memory signal (in-process) |
| Long-lived bash | `LocalShellExecutor` is what we want | `Microsoft.Agents.AI.Tools.Shell` (gated by `#if NET`) |
| Restart safety (chat history) | None. Restart loses in-flight work. | `AgentSession.SerializeSessionAsync`/`DeserializeSessionAsync` covers the chat half. Worktree / PR / push state is on us (see "Restart safety" section below). |
| Issue / sprint / skill CRUD | SQLite + dashboard (UNCHANGED) | Same. The agents read via the same `IssueStore` / `SkillStore` / `SprintStore` interfaces. |
| Dashboard | Kestrel, vanilla JS, 5 tabs (UNCHANGED) | Same. We expose a minimal `/api/agents/{id}/message` faÃ§ade backed by `AIAgent.RunAsync`. |

### New components we will write

- **`SqliteAgentSkillsSource`** â€” subclass of `AgentSkillsSource` (or composition via `AgentSkillsProviderBuilder.UseSource(...)`) that reads skills from our existing `skill` table and surfaces them through the standard `load_skill` / `read_skill_resource` / `run_skill_script` tools.
- **Git AIFunctions** â€” `git_worktree_add`, `git_commit`, `git_push`, `worktree_remove` â€” `AIFunctionFactory.Create(...)` over the existing `GitWorktreeService`.
- **PR AIFunction** â€” `open_pull_request` wrapping Octokit.
- **PR merge signal** â€” an in-process `Channel<PRMergedEvent>` consumed by an executor; or, on DurableTask, an external event subscription.
- **Provider abstraction** â€” `IChatClient` registration based on configuration; thin shim around whatever Microsoft package we choose.
- **Workflow host** â€” composition root that builds the per-sprint workflow and runs it via `InProcessExecution.RunStreamingAsync`.

### What we keep verbatim

- `Core/IssueStore.cs`, `Core/AgentStore.cs`, `Core/SkillStore.cs`, `Core/SprintStore.cs` â€” schema, CRUD, invariants, agent-message bus all stay.
- `AgentTools/GitWorktreeService.cs` â€” moves under `Tools/`, same API.
- `Dashboard/` â€” keep the dashboard; expose one new endpoint (`POST /api/agents/{kiloName}/messages` is replaced by `POST /api/issues/{id}/messages` routed through `AIAgent.RunAsync`).
- `AppDomain/ProcessExit` shutdown handling â€” unchanged.



## Phased migration

Five phases. Each phase is independently shippable and reversible.

| Phase | Scope | Why this order |
|---|---|---|
| **P0** (closed 2026-06-30) | Add the MAF NuGet packages as prerelease. Wire IChatClient into a new Agents/ project. Replace AcpClient with a MafAgentRunner that wraps `AIAgent.RunAsync(session, prompt)` (in-process, no HTTP). IAgentRunner interface (`Task<AgentRunResult>`) keeps the orchestrator unchanged. | Foundations; all later phases depend on this. |
| **P0.5** | Add Vision table + ision.md import; UI tab Vision; existing engineering loop reads ision.md once at startup. | Cheap. Validates the storage path. |
| **P1** (existing) | Skills loading works. | Already designed. |
| **P1.4** | Intake agent: one persistent HarnessAgent per project. UI: Intake tab with chat stream + Accept epic button. Operator-driven conversation; agent writes epics (issue.type='epic') into the active sprint. Uses HarnessAgent bundled TodoProvider / FileMemoryProvider / hosted web search / LoopAgent. | The intake path is the first end-user-facing agent. We get it in before the rest of the cross-functional work so the operator can start using the system. |
| **P1.5.a** | Spec table + UI tab Specs (read-only). | Validate the spec model before giving product agent write access. |
| **P1.5.b** | Product agent writes spec rows via AIFunctions. Operator approves via dashboard. | Loop is closed but human-gated. |
| **P2.a** | design_artifact table + UI tab Design + Designer agent. | Designer is a synchronous workflow node. |
| **P2.b** | rtist_output table + MeshyClient + UI tab Art + Artist agent. | Meshy is the long pole; isolate its failures. |
| **P3** (existing) | Engineering dispatch becomes a MAF workflow. | Already designed. |
| **P3.5** | issue_groomer_run table + Groomer agent on schedule. | Auto-groomer decomposes open epics. |
| **P4** (existing) | DurableTask. | Already designed. |

P1.5 is a sub-plan inserted between P1 and P2. The P2 / P3 / P4 numbering is preserved.

### Phase 0 — Package & skeleton (closed 2026-06-30)

Add the MAF NuGet packages as prerelease. Wire `IChatClient` into a new `Agents/` project. Replace `AcpClient` with a `MafAgentRunner` that wraps `ChatClientAgent.RunAsync(message, session?)` (in-process, no HTTP). `IAgentRunner` interface (`Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, CancellationToken ct)`) keeps the orchestrator unchanged.

Reuse `RoleAgentRegistry` to build the `ChatClientAgent` per role. The "kilo .md" file content becomes the `instructions:` parameter. Roles that don't have an LLM provider (dev) still log the prompt.

**Outcome (2026-06-30):** `OrchestratorAgent` calls `IAgentRunner.RunAsync(...)` and no longer references `AcpClient`/`AcpProcessManager`. The kilo/ACP files are deleted. The no-op commit branch captures `modelResponse` metadata; the runner's `Elapsed` TimeSpan flows through to the dashboard `AcpSessionCompleted` event. 89/89 tests pass. P0.5+ proceeds.

Deliverable: one existing scenario (e.g., the ecs-1 task) runs end-to-end through MAF, with the dashboard still showing the result. **Test outline:** the integration test under tests/PortHorizon.Agents.Tests/Integration/Phase0Tests.cs enqueues a fixture issue with a stubbed IChatClient that returns a scripted ChatResponse; asserts (a) MafAgentRunner.RunAsync returns a non-empty response, (b) the orchestrator's no-op branch transitions the issue to `Completed` with the response text in `modelResponse` metadata, (c) the integration test runs without kilo installed. We do NOT exercise git worktree creation, PR creation, or any real LLM in Phase 0 - those are Phase 2.

### Phase 1 â€” Skills actually do something

Add `SqliteAgentSkillsSource` (or the equivalent builder composition). Skills are now loaded into the agent's context. The `SkillStore` CRUD UI on the dashboard keeps working. We verify by enqueueing a task that asks the agent to load a skill and use it.

### Phase 2 â€” Git + PR tools

Add `git_*` and `open_pull_request` `AIFunctions`. The agent now has real tools and is no longer text-only. The "agent commits and pushes a branch" loop becomes agent tool calls instead of orchestrator-side calls.

This is the most invasive phase: the dispatch shape changes from "orchestrator-driven worktree â†’ call kilo â†’ orchestrator commits" to "agent does all of it via tools."

### Phase 3 â€” Workflows

Replace the `DispatchCycleAsync` loop with a `WorkflowBuilder`. One sprint = one workflow run. Per-issue executor = the agent run. Fan-out via `AddFanOutEdge`. `PRWatcher` becomes an executor that waits on the merge signal.

This is the phase where we gain parallel dispatch as a framework feature instead of a hand-rolled semaphore.

### Phase 4 â€” Durable (optional)

Move from `InProcessExecution` to `Microsoft.Agents.AI.DurableTask`. The PR merge signal becomes a real webhook. Orchestrator can restart without losing sprints. This is the longest phase and the one most exposed to DurableTask API surface changes (it's still being shaped).

We make this an opt-in tier: P0..P3 stay on the in-process runtime; P4 is enabled by configuration.

### Rollback strategy

Each phase is behind a feature flag (`Orchestrator:Runtime=Kilo|Maf`, `Orchestrator:Workflow=Loop|Graph`, `Orchestrator:Execution=InProcess|Durable`). Kilo stays in the binary, behind an interface, until Phase 4 ships. A bad MAF change flips the flag, not a rollback.



## Risks

1. **Skills surface is public but evolving.** As of dotnet 1.11.x the shipped surface includes AgentSkillsSource, AgentFileSkillsSource, AgentInMemorySkillsSource, AgentSkillsProvider, AgentSkillsProviderBuilder, plus decorator types (FilteringAgentSkillsSource, CachingAgentSkillsSource, AggregatingAgentSkillsSource, DeduplicatingAgentSkillsSource). We can subclass AgentSkillsSource directly. The design doc at docs/decisions/0021-agent-skills-design.md is dated 2026-03-23 and labeled proposed - pin a prerelease version, watch for breaking changes between minors, and contribute any improvements we make back upstream.
2. **`AgentSession` and untrusted store.** `AgentSession`'s xmldoc explicitly warns: "Treat restoring a session from an untrusted source as equivalent to accepting untrusted input. A compromised storage backend could alter message roles to escalate trust, or inject adversarial content that influences LLM behavior." For us: when we load `AgentSession` from the issue's `metadata_json`, sanitize role tags, and never accept session blobs from outside the orchestrator process.
3. **`AgentSession` not reusable across agents.** The same xmldoc says "an AgentSession may not be reusable across different agents." Our agent instances are constructed per-issue with the same factory shape; we need to keep that invariant or reload the session under a new agent.
4. **Experimental attributes + HarnessAgent IS adopted (intake role only).** HarnessAgent and parts of Microsoft.Agents.AI.Tools.Shell are tagged [Experimental(DiagnosticIds.Experiments.AgentsAIExperiments)]. We adopt HarnessAgent **only for the intake role** (single persistent project-aware conversation that needs HarnessAgent's bundled todo / file-memory / hosted-web-search / loop conveniences). Engineering, designer, artist, and groomer agents use plain ChatClientAgent + AIFunctions. Pin the HarnessAgent prerelease version; treat the API as frozen between minor upgrades. Add <NoWarn>AGENTS_AI_EXPERIMENTS</NoWarn> for now.
5. **DurableTask on a vanilla `IHost`.** The README and `Workflows/` folder are opaque on whether `Microsoft.Agents.AI.DurableTask` works in a console host with the in-process DTS sidecar. Validate with `dotnet/samples/04-hosting` before designing around it.
6. **Skills `SKILL.md` schema mismatch.** The MAF skills design uses YAML frontmatter with `name`, `description`, `license?`, `compatibility?`, `allowedTools?`. Our existing `.kilo/agents/*.md` files don't match this. We either (a) rewrite the files to match, (b) write a custom `AgentFileSkillsSource` subclass that reads our schema, or (c) keep skills in SQLite and use the in-memory provider.
7. **No ACP.** kilo's wire protocol is gone. Anything in our codebase that referenced `AcpClient`, `AcpSession`, or the kilo JSON shape becomes dead code (kept behind an interface for the rollback window).
8. **kilo files.** The `.kilo/agents/*.md` files stay on disk during the migration (they're the source of `instructions:` for the `ChatClientAgent`). Long-term, the dashboard's Agent editor should write both the SQLite row and the .md file, like before.




## Test strategy

- **Unit tests on every adapter.** Continue `tests/PortHorizon.Agents.Tests/` as the home for unit tests. New components (`MafAgentRunner`, `SqliteAgentSkillsSource`, git/PR `AIFunction`s, workflow host) ship with one-off unit tests covering happy path, error path, and the "what happens if the underlying IChatClient throws mid-stream" path.
- **Stubbed `IChatClient` for replay-style tests.** Microsoft has `Microsoft.Agents.AI.Testing` on NuGet (verify before adopting; if absent, roll a `TestChatClient : DelegatingChatClient` that returns scripted `ChatResponse` objects from a queue). All agent logic — skills loading, tool dispatch, prompt assembly — runs against the stub; no real LLM in unit tests.
- **One in-process integration test per phase.** Each phase (`phase-{0..4}-integration-tests`) spins up the real workflow with a stub `IChatClient`, an in-memory `SqliteAgentSkillsSource`, and a stub `Octokit` PR client. The test asserts: issue claimed → worktree created → agent run → commit pushed → PR opened → issue marked Completed. This is the "did I wire all the pieces correctly" test.
- **No live-model regression tests.** A 4o / Claude / Anthropic session for "regression" is not viable (non-deterministic, expensive, slow). Keep that out of CI. Live smoke tests are manual checks before each release.

## Observability

MAF ships OpenTelemetry integration. We wire it from Phase 0:

- `IChatClient` exposes `UseOpenTelemetry(sourceName: "PortHorizon.Orchestrator")` so every LLM call is automatically traced with prompt + completion tokens.
- `ChatClientAgent` (per issue) gets `UseOpenTelemetry(sourceName: $"PortHorizon.Agent.{IssueId}")`.
- `Microsoft.Extensions.AI` adds `OpenTelemetryProtocol` support so we ship to the same OTLP endpoint the orchestrator already uses (`OpenTelemetry:Endpoint` in `appsettings.json`).
- The dashboard gets two new tabs later: **Traces** (recent spans, filter by issue / agent) and **Token usage** (per-day, per-role, per-issue). These are P2/Phase-3 work — not Phase 0 — but the wiring has to start in Phase 0 so we don't have a retroactive trace gap.

## Restart safety — what we actually get vs. what we have to build

`AgentSession.SerializeSessionAsync` / `DeserializeSessionAsync` returns a `JsonElement` that captures **chat history only**. It does *not* capture:

- worktree path, branch name, base SHA
- PR URL and merge status
- any in-progress tool state (e.g., a partially-pushed commit)
- operator-inbox state

If the orchestrator crashes mid-dispatch, on restart:
- We *do* reload the agent's conversation history cleanly (Phase 0).
- We *don't* automatically resume a half-completed commit/push — that requires us to write a checkpoint into `metadata_json` at each phase boundary (worktree acquired, files committed, branch pushed, PR opened).
- For "PR merged" signals, we either keep the existing 30s poll (Phase 3) or upgrade to external events (Phase 4 / DurableTask).

The "Restart safety" row in the stays-vs-changes table is misleading: MAF gives us half. The other half is on us. We write the checkpoints; MAF gives us the agent conversation.

## .kilo/agents/*.md fate — pick one

Three options, all explicit:

(a) **Dual-write forever.** Dashboard Agent editor writes both the SQLite row AND the .md file. Cheap; preserves human-readable git-diff of role definitions. The .md is dead-on-arrival as far as the runtime is concerned (Phase 0 reads it once at startup into `instructions:`), but humans can read it.

(b) **One-time migration, then SQLite is source of truth.** Phase 0 reads every .md into `instructions:` once, persists the result, deletes the .md files. Dashboard edits SQLite only. Cleanest, but loses human-readable role definitions unless we add a SQL→MD exporter.

(c) **.md is source of truth.** Dashboard Agent editor edits the .md file and the SQLite row in lockstep. Cheap to read (file on disk), git-tracked, but SQLite becomes a cache. Re-introduces the dual-write problem we already have.

**Recommendation: (a) for v1**, revisit at v2. .md files as human-readable docs alongside the SQLite row is a reasonable convention.

## Dashboard surface decomposition

The dashboard needs four MAF-shaped changes, distributed across phases:

| Phase | Surface added | What it does |
|---|---|---|
| Phase 0 | `/api/agents/{kiloName}/messages` → `/api/issues/{id}/messages` (route through `MafAgentRunner`) | Operator inbox routing, one-shot. |
| Phase 0 | Issue detail modal shows `AgentResponse.Text` instead of "modelResponse from metadata" | Display what the LLM said. |
| Phase 2 | Issue detail modal streams tool-call history (git_worktree_add, git_commit, …) | Show what the agent did during dispatch. |
| Phase 3 | Per-issue conversation tab — scrollback of `AgentResponseUpdate` from the in-process run | Replay what happened on this issue. |
| Phase 4 | Per-sprint workflow view — `WorkflowEvent` stream in real time | Show concurrent dispatches in a sprint. |

The five-tab structure (Tasks / Backlog / Sprints / Agents / Skills) stays; the per-issue detail modal is what grows over time.

## Restart-safety of session state — what to persist ourselves

In addition to `AgentSession` serialization, we write checkpoints into `issue.metadata_json`:

| Phase boundary | Persisted key | What it represents |
|---|---|---|
| After worktree acquire | `worktreePath` | The worktree dir, so re-entry can cd into it. |
| After commit | `commitSha` | The committed SHA so re-entry knows what's been shipped. |
| After push | `branchSha` | The branch tip so re-entry can compare and decide whether to push again. |
| After PR open | `prNumber`, `prUrl` | Re-entry can poll this PR instead of opening a new one. |
| After merge | `mergedAt` | Terminal state for the issue. |

These are reads-only-from-the-issue's perspective. Writes happen at the phase boundary inside the orchestrator. The agent doesn't know about them.


9. **GitHub MCP tool overlap.** If we ever use the GitHub Copilot provider or any provider that bundles the GitHub MCP server, the agent will receive *built-in* GitHub tools (PR creation, issue filing, etc.) that overlap with our hand-rolled open_pull_request AIFunction. Pick: (a) let the agent use provider-native GitHub tools and keep Octokit only for the merge-watcher; (b) disable provider-native GitHub tools in our ChatClientAgentOptions.Tools and own the GitHub surface ourselves. (b) is the default; (a) is cheaper if we trust the provider tool-set.
10. **DurableTask container dependency.** Phase 4 requires either Azure-hosted Durable Functions, docker run mcr.microsoft.com/dts/dts-emulator, or a self-hosted DTS. There is no in-process DTS. Accepting Phase 4 means accepting a Docker-or-Azure dependency in the deploy story. The current PortHorizon.Agents binary has no such dependency. Phase 4 is opt-in; the orchestrator can ship P0..P3 without it.
11. **Naming.** The old RoleAgentRegistry + .kilo/agents/*.md model has the word kilo in several places (e.g. the kiloName column on AgentStore). The MAF migration is the natural point to rename to provider/displayName. We do this as part of Phase 0 to avoid carrying the wrong name into the MAF era.


## Cross-functional agents (product, design, artist, groomer)

The migration is not only about replacing the engineering agent runtime. It also brings product, design, and art into the same agentic plane. The shape of this work was set by the user:

- **Product agent is a pre-step before engineering.** When a new feature is requested (or the vision doc changes), product designs the spec, then engineering picks it up. Engineering is downstream of product.
- **Designer agent is a workflow node that runs alongside product.** Designer hands off a finished visual to artist; artist hands off to engineering. Specs flow product -> design -> art -> engineering.
- **Intake agent is the entry point.** The user types in the Intake tab. The agent uses project context (vision, sprint, issues, agent defs) to help shape an idea into a well-scoped epic. The user clicks Accept epic and the workflow emits a sprint-issue row plus an issue of 	ype='epic'. The intake workflow is the only one that uses HarnessAgent.
- **Artist agent uses Meshy.ai as its tool** for 3D-asset generation (text-to-3D, image-to-3D, retexture, animation).
- **Issue groomer is automatic** and runs on a schedule. It reads `VISION.md` plus the existing issues plus recent PR comments, then rewrites, adds, or retires issues to match the vision.

### Dashboard tabs (full enumeration, post-P1.4)

| Tab | What | Phase |
|---|---|---|
| Tasks | Issue list, filterable. Issue detail modal shows modelResponse and tool-call history once P2 lands. | P0 |
| Backlog | Same as Tasks but unfiltered. | P0 |
| Sprints | List of sprints with goal + in-progress-count vs cap. Set-active + archive + add-issue controls. | P0 |
| Agents | One row per agent (coredev, product, designer, rtist, groomer, plus the project-specific intake). Send-message modal per row. | P0 (engineering) / P1.4 (intake) / P1.5.b (product) / P2.a (designer) / P2.b (artist) / P3.5 (groomer) |
| Skills | Global + per-agent skill catalog. | P0 |
| **Intake** | Chat window with the intake agent. Persistent conversation; operator types here; agent uses project context to help shape an idea. When the operator clicks Accept epic, the agent calls db_create_epic_draft and emits a sprint-issue row plus an issue of 	ype='epic'. Drafts are visible to the operator before commit. | **P1.4** |
| Vision | Latest vision doc, with diff vs. previous. Replan button to trigger product agent. History of replans. | P0.5 |
| Specs | List of specs with status. Click into one to see: problem, solution, acceptance, design artifacts (image grid), artist output thumbnails (Meshy GLB previews). | P1.5.a |
| Design | Image grid of all design_artifact rows, filter by spec. | P2.a |
| Art | List of rtist_output rows with status pills (PENDING / IN_PROGRESS / SUCCEEDED / FAILED), credits used, preview GLB. Click to download. | P2.b |
| Groomer | Timeline of issue_groomer_run rows. Click to see the diff (which issues / epics were created / closed / updated and how epic-to-task decomposition was partitioned). Manual Re-run button. | P3.5 |
| Traces | Recent OpenTelemetry spans, filter by issue / agent. | P3 |
| Token usage | Per-day, per-role, per-issue cost rollup. | P3 |

### Data model additions (all in the same SQLite DB)

```sql
-- vision: top-level product document. One row per major version; groomer reads/writes this.
CREATE TABLE vision (
    id            TEXT PRIMARY KEY,
    title         TEXT NOT NULL,
    body          TEXT NOT NULL,
    goal          TEXT,
    non_goals     TEXT,
    success_metrics TEXT,
    author        TEXT NOT NULL,
    version       INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);

-- spec: per-feature spec written by the product agent.
CREATE TABLE spec (
    id            TEXT PRIMARY KEY,
    vision_id     TEXT REFERENCES vision(id),
    feature_name  TEXT NOT NULL,
    problem       TEXT NOT NULL,
    solution      TEXT NOT NULL,
    acceptance    TEXT NOT NULL,
    out_of_scope  TEXT,
    design_ref    TEXT,
    art_ref       TEXT,
    status        TEXT NOT NULL DEFAULT ''draft'',
    author        TEXT NOT NULL,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX ix_spec_status ON spec(status);
CREATE INDEX ix_spec_vision ON spec(vision_id);

-- design_artifact: visual assets produced by the designer agent.
CREATE TABLE design_artifact (
    id            TEXT PRIMARY KEY,
    spec_id       TEXT REFERENCES spec(id),
    kind          TEXT NOT NULL,
    storage_path  TEXT NOT NULL,
    format        TEXT NOT NULL,
    description   TEXT,
    author        TEXT NOT NULL,
    created_at    TEXT NOT NULL
);

-- artist_output: 3D assets produced via Meshy.ai.
CREATE TABLE artist_output (
    id                TEXT PRIMARY KEY,
    spec_id           TEXT REFERENCES spec(id),
    design_ref        TEXT,
    meshy_task_id     TEXT NOT NULL,
    meshy_endpoint    TEXT NOT NULL,
    meshy_status      TEXT NOT NULL,
    preview_url       TEXT,
    model_url         TEXT,
    texture_urls_json TEXT,
    credits_used      INTEGER,
    prompt            TEXT,
    negative_prompt   TEXT,
    author            TEXT NOT NULL,
    created_at        TEXT NOT NULL,
    completed_at      TEXT
);
CREATE INDEX ix_artist_status ON artist_output(meshy_status);
CREATE INDEX ix_artist_spec ON artist_output(spec_id);

-- issue_groomer_run: audit log of every groomer pass.
CREATE TABLE issue_groomer_run (
    id              TEXT PRIMARY KEY,
    vision_id       TEXT REFERENCES vision(id),
    started_at      TEXT NOT NULL,
    finished_at     TEXT,
    issues_created  INTEGER NOT NULL DEFAULT 0,
    issues_closed   INTEGER NOT NULL DEFAULT 0,
    issues_updated  INTEGER NOT NULL DEFAULT 0,
    rationale       TEXT,
    status         TEXT NOT NULL DEFAULT ''running''
);
```

`IssueStore` (the existing one) gets a new column: `issue.spec_id TEXT REFERENCES spec(id)`. An issue is the engineering-side breakdown of a spec.



### MeshyClient (a new Tools/ class)

Encapsulates the Meshy HTTP API. The artist agent gets these as `AIFunction`s:

| `AIFunction` name | Wraps | Async? | When called |
|---|---|---|---|
| `meshy_text_to_3d_preview` | `POST /openapi/v2/text-to-3d` (mode=preview) | yes (returns task id immediately) | Start a model. |
| `meshy_text_to_3d_refine` | `POST /openapi/v2/text-to-3d` (mode=refine) | yes | Apply texture after preview is SUCCEEDED. |
| `meshy_image_to_3d` | `POST /openapi/v1/image-to-3d` | yes | Render from a design mockup. |
| `meshy_get_task` | `GET /openapi/v2/text-to-3d/:id` | sync (poll) | Check status. |
| `meshy_subscribe_task` | `GET /:id/stream` (SSE) | async stream | Webhook-equivalent; subscribe to completion. |
| `meshy_remesh` / `meshy_retexture` / `meshy_rig` / `meshy_animate` | one each | per endpoint | Post-processing. |
| `meshy_text_to_image` / `meshy_image_to_image` | one each | per endpoint | Concept / iteration images that feed the 3D pipeline. |

**Wire details** (from the Meshy API docs):
- Auth: `Authorization: Bearer <MESHY_API_KEY>` header. Key from `https://www.meshy.ai/settings/api`.
- Async model: POST returns a task id. Status is `PENDING` -> `IN_PROGRESS` -> `SUCCEEDED` / `FAILED` / `CANCELED`.
- Output: GLB / FBX / USDZ / OBJ / STL / 3MF. **Default generates all formats except 3MF;** pass `target_formats` to restrict.
- Costs (verified against `docs.meshy.ai/api/pricing.md`): Text-to-3D Preview 20cr (Meshy 6) / 5cr (other); Refine 10cr; Image-to-3D 20cr without texture / 30cr with texture (Meshy 6) or 5/15cr (other); Multi-Image-to-3D same as Image-to-3D; Remesh 5cr; Auto-Rigging 5cr; Animation 3cr; Convert 1cr; Resize 1cr; Retexture 10cr. **Failed tasks are refunded** (`consumed_credits` returns 0); success/failure must be reconciled against `consumed_credits` from the response, not assumed.
- Asset retention: 3 days for Pro / Studio, longer for Enterprise. **Asset URLs in the response are signed and expire** (the response example shows `?Expires=...`). The artist must download the GLB to `.portHorizon/artifacts/<spec_id>/<artist_output_id>.glb` **promptly after SUCCEEDED**, not lazily.
- Rate limits: Pro/Studio = 20 req/s. **Pro tier queue depth = 10 tasks;** Studio = 20. The artist must respect `Meshy:MaxConcurrent` to avoid filling the queue and timing out on `429`.
- Billing endpoint: `GET /openapi/v1/balance` returns `{"balance": <integer>}` - current remaining credits, **not** a per-month ledger. Monthly budget enforcement is done client-side by summing `consumed_credits` from each `artist_output` row.
- Webhooks: real, configured in the dashboard, up to 5 active per account, HTTPS-only, require `<400` response, auto-disable on repeated failures. **Webhooks are Meshy recommended primary completion signal** (better than polling/SSE at scale per their docs).
- SSE fallback: `GET /<endpoint>/:id/stream` returns NDJSON-style events. We use this as a fallback when the webhook endpoint is unavailable.


### New MAF agents and their AIFunction set

**Intake agent** (IntakeAgent : HarnessAgent - one instance per project, persistent)
- Instructions: structured prompt saying you are a project-aware intake agent for PortHorizon; you help the user turn rough ideas into well-scoped epics.
- AIFunctions: db_read_vision, db_list_specs, db_list_open_issues, db_read_active_sprint, db_list_recent_pr_comments, db_create_epic_draft (writes an issue row with type=epic, status=Draft, parent_id NULL, sprint_id pointing at the active sprint), db_promote_epic (transitions draft to Pending, emits a sprint-issue row plus an SSE event).
- One intake agent instance per project (single persistent conversation). Survives orchestrator restarts via AgentSession.SerializeSessionAsync. Intake is the ONLY role where the long-running session matters; engineering, designer, artist, and groomer agents get a fresh short-lived session per turn.
- Uses HarnessAgent: TodoProvider for tracking the conversation through multi-turn shaping, FileMemoryProvider for noting follow-ups across turns, hosted web search for ADRs and external docs, LoopAgent so the agent can iterate on its own draft before responding.
- Trigger: the user types in the Intake tab. Not scheduled, not on a trigger. The user is the trigger. The conversation pauses between turns via WaitForExternalEvent of the operator-message event and resumes on the next send.
- Output is an epic issue (a row in issue with type=epic, parent_id NULL, sprint_id pointing at the active sprint, status=Pending). Not a spec. The groomer in P3.5 reads open epics and decomposes them into engineering tasks.


**Product agent** (`ProductAgent : ChatClientAgent`)
- Instructions: structured prompt that says "you are a product manager; given VISION.md and the current sprint, write a `spec` row in the database; do not write code."
- AIFunctions: `db_get_vision`, `db_list_specs`, `db_save_spec`, `db_list_recent_pr_comments`.
- One product agent instance per vision-bump; output is one or more `spec` rows.
- Does **not** run inside a sprint workflow. Runs on a trigger: vision change OR explicit `/api/vision/replan` request OR schedule (every N hours). It writes specs but does not enqueue issues.

**Designer agent** (`DesignerAgent : ChatClientAgent`)
- Instructions: given a `spec` row, produce a visual: wireframe (low-fidelity), mockup (high-fidelity), or component spec. Output goes to `design_artifact` rows.
- AIFunctions: `db_get_spec`, `db_save_design_artifact`, `render_html_wireframe` (returns HTML the dashboard renders inline), `render_svg` (returns inline SVG).
- Runs after a spec is created. Output is one or more `design_artifact` rows.

**Artist agent** (`ArtistAgent : ChatClientAgent`)
- Instructions: given a `spec` (and optional `design_artifact` reference), decide what 3D assets are needed. For each, pick the Meshy endpoint + parameters, kick off the job, and persist the resulting `artist_output` row.
- AIFunctions: the full MeshyClient set above + `db_save_artist_output`, `db_get_design_artifact`, `db_persist_artifact_file` (downloads the GLB to disk).
- Runs after a design is approved. Can run multiple parallel Meshy tasks per spec.
- **Important:** Meshy tasks are long (preview ~1-2 min, refine another 1-2 min). The artist agent must NOT block the engineering dispatch on a Meshy task. It writes the `artist_output` row in `PENDING` / `IN_PROGRESS` state and engineering can pick up the issue in parallel. When Meshy''s `/stream` endpoint emits completion, the artist agent (a background subscriber) updates the row to `SUCCEEDED`.

**Issue groomer** (`GroomerAgent : ChatClientAgent`)
- Instructions: given VISION.md, open epics (issue.type='epic' with status='Pending'), and recent PR comments, decompose each epic into a sprint_issue row plus one or more engineering tasks (rows in issue with 	ype='task' and parent_id pointing at the epic). Closes completed-but-on-the-sprint issues that are no longer relevant. Never touches code.
- AIFunctions: db_read_vision, db_list_open_epics, db_list_specs, db_list_open_issues, db_create_task, db_close_issue, db_update_issue, db_log_groomer_run.
- Trigger: every N hours (configurable, default 6) OR on `VISION.md` change OR on explicit `/api/groom` request.
- Idempotency: writes a `issue_groomer_run` row with counts and rationale. Re-runs are safe; the agent diffs state before proposing changes.



### Workflow shape: intake -> epic -> groomer -> engineering (with designer and artist as opt-in sidecars)

A single `WorkflowBuilder` per vision-change event:

```
                  vision-bump event
                         |
                  product agent
                  (writes spec rows)
                         |
              spec:status=ready
                         |
                  designer agent
                  (writes design_artifact rows)
                         |
         design_artifact:kind in {mockup, wireframe, component-spec}
                         |
                  artist agent
                  (writes artist_output rows, kicks off Meshy)
                         |
              artist_output:status=SUCCEEDED
                         |
                  product agent (or operator)
                  flips spec:status -> claimed
                         |
                  engineering dispatch loop
                  (existing in-process orchestrator)
```

