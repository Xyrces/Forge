
# Microsoft Agent Framework migration

Status: design draft (2026-06-29). Goal: replace the kilo-based runtime in PortHorizon.Agents with Microsoft Agent Framework (MAF) as the agent runtime, while keeping the SQLite-backed issue/agent/skill/sprint store and the Kestrel dashboard.

## Why we are doing this

The current orchestrator (P0 + P1) drives agents by shelling out to `kilo serve` and talking its bespoke HTTP+JSON protocol. Two failure modes:

1. **Wire fragility.** kilo's POST `/session/<id>/message` does not return until the agent finishes. We side-stepped this with a parallel poll-and-cancel harness in `AcpClient.PromptAsync`. The fix works but is a workaround for a quirk of a tool that is not our problem to fix.
2. **Provider coupling.** We are locked into `kilo acp`'s tool protocol, prompt shapes, and session IDs. Switching to a different LLM provider today means writing a parallel `AcpClient` for each one.

MAF removes both. It is the direct successor to both Semantic Kernel and AutoGen, designed by the same Microsoft teams to consolidate the two. It is open-source (.NET + Python), production-graded (1.x at dotnet 1.11.1 on 2026-06-25; 11.8k stars), and has explicit migration documentation for both SK and AutoGen.

It also offers primitives we currently lack and would otherwise have to build:

- `AIAgent` + `AgentSession` with explicit serialize/deserialize for restart-safety.
- `Workflows` (graph-based fan-out / fan-in) that replace our hand-rolled `DispatchCycleAsync`.
- `Microsoft.Agents.AI.Harness` (`HarnessAgent` + `HarnessAgentOptions`) â€” a higher-level wrapper that already covers ~70% of what our `OrchestratorAgent` does.
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
| Sprint dispatch | `ReadyAsync` filtered by sprint; one process per task | `WorkflowBuilder` per sprint, `AddFanOutEdge` over `ChatClientAgent` instances, one durable entity per issue (future) |
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

### Phase 0 â€” Package & skeleton

Add the MAF NuGet packages as prerelease. Wire `IChatClient` into a new `Agents/` project. Replace `AcpClient` with a `MafAgentRunner` that wraps `AIAgent.RunAsync(session, prompt)` (in-process, no HTTP). `IAgentRunner` interface (Task-oriented, with a `DrainInbox` and a `Probe` method) keeps the orchestrator unchanged.

Reuse `RoleAgentRegistry` to build the `ChatClientAgent` per role. The "kilo .md" file content becomes the `instructions:` parameter. Roles that don't have an LLM provider (dev) still log the prompt.

Deliverable: one existing scenario (e.g., the `ecs-1` task we used during live testing) runs end-to-end through MAF, with the dashboard still showing the result.

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

1. **Skills surface is unstable.** `docs/decisions/0021-agent-skills-design.md` is dated 2026-03-23 and marked `proposed`. The decision-outcome section says "All agent-skill-related classes are made internal to minimize the public API surface while the feature matures. This leaves two public entry points: AgentSkillsProvider and AgentSkillsProviderBuilder." Before building `SqliteAgentSkillsSource` on top of this, we need to confirm the shipped NuGet surface â€” if `AgentSkillsSource` is internal, we either (a) put our source in the same assembly or (b) contribute a PR upstream.
2. **`AgentSession` and untrusted store.** `AgentSession`'s xmldoc explicitly warns: "Treat restoring a session from an untrusted source as equivalent to accepting untrusted input. A compromised storage backend could alter message roles to escalate trust, or inject adversarial content that influences LLM behavior." For us: when we load `AgentSession` from the issue's `metadata_json`, sanitize role tags, and never accept session blobs from outside the orchestrator process.
3. **`AgentSession` not reusable across agents.** The same xmldoc says "an AgentSession may not be reusable across different agents." Our agent instances are constructed per-issue with the same factory shape; we need to keep that invariant or reload the session under a new agent.
4. **Experimental attributes.** `HarnessAgent` and the Tools.Shell package are tagged `[Experimental(DiagnosticIds.Experiments.AgentsAIExperiments)]`. Pin a version, vendor it, or budget for breaking changes every minor. Add `<NoWarn>AGENTS_AI_EXPERIMENTS</NoWarn>` for now.
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

## Open questions


1. **Provider.** GitHub Copilot SDK for dev (closest parity with current kilo setup), or Microsoft Foundry all the way? We need to verify which one works on this host without bringing in `az login` friction.
2. **Dashboard integration depth.** Do we (a) keep `:4097` as the only UI and expose agent messages via a thin faÃ§ade, or (b) lean on MAF's `Hosting.AspNetCore` and A2A bridge so the dashboard becomes a thin client? (a) keeps the existing dashboard untouched; (b) is the long-term MAF-shaped path.
3. **LocalShellExecutor vs our hand-rolled bash tool.** MAF ships a shell executor in `Microsoft.Agents.AI.Tools.Shell` (`.NET`-only, `#if NET`). If our shell needs are narrow (run `dotnet`, `git`, `gh`), we may not need it. If we need richer sandboxing (DockerShellExecutor), we adopt it.
4. **PR merge signal in in-process mode.** Without DurableTask, how do we signal "PR merged"? Polling (status quo), GitHub Actions workflow that pushes to our HTTP webhook (clean but requires GH-side config), or a local file watcher (hacky). Decision depends on whether we ever skip Phase 4.
5. **Performance / cost.** MAF eliminates the kilo round-trip but adds the cost of in-process agent invocations. We need to model: tokens per dispatch, wall time per dispatch, max concurrent dispatches. We don't have numbers yet.
6. **Observability.** MAF has built-in OpenTelemetry. We should pipe to the same OTLP endpoint we configured for the orchestrator and add dashboard tabs for traces.
7. **What happens to `docs/embedded-issues.md` and the IssueStore schema?** Nothing changes. The P0 plan lives on as the storage layer for MAF's run-time needs.

## Out of scope

- Switching to Microsoft's Python Agent Framework (we're .NET-only).
- Replacing the dashboard UI with Microsoft's DevUI.
- Building custom Durable entities before validating the in-process path.
- Migrating kilo's per-role .md frontmatter to MAF's `SKILL.md` schema in this phase. That's a follow-up after we know the shape is stable.






