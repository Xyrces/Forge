# Sprint planning audit

Review baseline: `8460dde0742640bb333c4540a255e1b7a3f4b2ce`, branch `codex/unattended-workflow-review`, 2026-11-17. Read-only production analysis; proposed behavior below is **not implemented or approved**. The audit used local isolated SQLite fixtures only, not the production Azure SQL database. References are repository-relative and refer to this baseline's sprint code.

## What the current system actually does

The concern is substantially right about the absence of outcome planning, but not literally that every sprint is an epic or every remediation task runs alone. Forge has a deterministic **provenance-based batch assembler**, plus a separate operator proposal path. It does not currently choose a bounded, independently demonstrable outcome from the whole backlog.

The August 8 rule in `AGENTS.md` supersedes the old solo-follow-up rule. Current grouping is:

| Work | Assembly group | Size | Sprint name/goal |
|---|---|---|---|
| Tasks under stories under a groomed spec | Spec ID, not necessarily epic ID | Every currently eligible task; no size cap | Spec title; parent epic description if present |
| Parentless groomed follow-ups | Root reached through `followUpOf` links | Up to 10 | Highest-priority member title; first three titles plus root context |
| Truly rootless groomed ad-hoc work | One selected task | One | Task title/description |

Evidence: `Orchestrator/Sprint/SprintAssembler.cs:904` eligibility, `:940` grouping, `:970` generated description, `:1026` choice, `:1037` caps, `:1112` parent walk, `:1149` follow-up root walk. The root walk chooses the first comma-separated ancestor and stops after 20 hops; it never examines meaning, acceptance criteria, shared files, or epic ancestry while walking follow-ups. Siblings under one spec can therefore produce separate follow-up themes.

The runtime flow is:

1. On a hint or backstop, sweep aged failures, then inspect the active sprint (`SprintAssembler.cs:234`).
2. If any member is not Completed/Closed, inject allowed ad-hoc work and retain that sprint. Failed/Blocked members prevent completion (`:254`, `:508`).
3. If all members finish, mark the sprint Completed, then materialize unconsumed follow-up drafts (`:277`, `:294`). Batch triage can merge findings or turn related findings into a new approved spec (`:615`, `:682`).
4. Close finished stories/epics, honor the assembly gate, and wait for **all** pending ungroomed follow-ups (`:310`, `:316`, `:335`).
5. Select Pending, unsprinted, unblocked engineering tasks. Require explicit grooming for ad-hoc tasks and a Groomed owning spec for spec tasks (`:904`, `:1179`).
6. Pick one group by its best member priority, then its recorded age; create Active sprint and attach tasks plus parent stories (`:1026`, `:1053`).
7. Dispatch only active-sprint members, with dependency readiness and per-role concurrency enforced separately (`Orchestrator/OrchestratorAgent.cs:207`, `:231`, `:248`, `:304`; `Core/IssueStore.cs:2037`).

Assembly is event-driven with a **15-minute production backstop**, not a mandatory five-minute wait (`Orchestrator/Composition/ForgeComposition.cs:732`). Several comments and the lifecycle skill still describe older FIFO/solo behavior; use the implementation above.

## Findings and concrete examples

### S1 — A theme is inferred from ancestry rather than a deployable outcome

**High confidence; product limitation, not evidence that all existing sprints are incoherent.** A groomed spec is assumed to be the theme. Every unblocked task in that spec is packed without sizing, acceptance mapping, role capacity, or cost consideration. Conversely, two findings from sibling tasks in the same feature form different `followup:<task>` groups. Identical titles or overlapping changes do not connect them. Rootless backlog work is always singleton. (`SprintAssembler.cs:948`, `:994`, `:1013`, `:1026`, `:1045`, `:1149`.)

Example: a spec contains 12 independent tasks across authentication, account settings, and email. The assembler includes all 12, although follow-up themes cap at 10. Two later authentication fixes filed from different original tasks do not share a sprint even when both support the same measurable outcome. This explains the user's observation without incorrectly claiming there is no follow-up packing.

### S2 — Dependency readiness is conflated with sprint membership

**High confidence; correctness gap against an independently deployable sprint contract.** `ListEligibleAsync` removes every task with any open blocker **before grouping** (`SprintAssembler.cs:924`). Thus A (schema) -> B (API) -> C (UI) inside one groomed spec becomes successive sprints, rather than one coherent sprint whose tasks execute in dependency order. The generated goal can still say to complete the whole spec/epic, while the actual membership contains only A.

This is unnecessary for dispatch safety: `IssueStore.ReadyAsync` already suppresses blocked execution and restricts dispatch membership (`Core/IssueStore.cs:2042`, `:2091`). A planning candidate can include dependencies without starting them prematurely. Distinguish planning eligibility from execution readiness.

### S3 — A late prerequisite with a parent can deadlock the active sprint

**High severity, high confidence.** While a sprint remains active, the assembler returns after its injection pass. Injection rejects every task with `ParentIssueId != null` before inspecting `blocks` relationships (`SprintAssembler.cs:254`, `:265`, `:275`, `:766`). If a new groomed spec task X blocks the active sprint's remaining task Y, X cannot be injected, Y is not runnable, and a new sprint cannot assemble. The same issue applies to existing related spec backlog that becomes necessary mid-sprint.

The normal `file_followup(blocksIssueId)` tool has a useful mitigation: it creates a parentless blocker directly inside the blocked task's active sprint (`AgentTools/FollowUpTool.cs:103`, `:128`). Dispatch still requires grooming (`OrchestratorAgent.cs:248`). That mitigation does not cover parented prerequisites introduced through grooming or other dependency edits. Recommend controlled dependency-closure repair for all task origins; do not turn off the grooming gate.

### S4 — Global follow-up grooming can idle otherwise-ready work

**High confidence; deliberate policy with a major unattended cost.** Any Pending task carrying `followUpOf` without `groomed=true` blocks all next-sprint assembly (`SprintAssembler.cs:335-370`). It need not belong to the chosen theme or the sprint that just completed. One low-priority documentation follow-up awaiting an unavailable groomer prevents a fully groomed independent feature from starting.

The waiting state is visible and the groomer is event-driven, which helps normal cases. But this is project-wide head-of-line blocking, not dependency readiness. Limit the hold to the candidate's required work, or expose a bounded planning phase that can deliberately defer nonblocking findings.

### S5 — Remediation does not automatically pull related backlog into the active sprint

**High confidence; partly intentional scope control.** A failed member normally remains in its existing active sprint; same-task PR rework does not create a special remediation sprint. Parentless task injection accepts a dependency into a member, P1, `blocker=true`, or an operator requeue marker. It does **not** match the active theme or semantic relatedness (`SprintAssembler.cs:797-827`). Follow-up findings normally remain drafts until completion; later themed grouping uses source ancestry.

Thus “never adds related backlog” is accurate for ordinary thematic matching, but too broad for dependency and urgent injection: those already work. The proposed replacement should preserve a committed goal and allow only necessary repairs/prerequisites mid-sprint. At the next planning boundary, pull complementary related work from anywhere in the groomed backlog within a bounded outcome. Avoid filling a sprint just because spare execution slots exist.

### S6 — Failure aging can turn abandonment into apparent completion

**High severity for unattended goal reporting; high confidence.** Current default `OrchestratorOptions.FailureAgingDays` is 7 (`Configuration/AgentOptions.cs:283`), wired to the assembler at `ForgeComposition.cs:740`. At every tick, old Failed rows are changed to Closed using `UpdatedAt` cutoff (`SprintAssembler.cs:446-459`), and Closed counts toward completion (`:528`). This is an intentional August 18 mechanism, not an accidental new change, but conflicts with the blanket “don't auto-clear Failed” wording elsewhere.

A failed required feature can age out, free dependent tasks because Closed is treated as satisfied, and allow the sprint/epic to complete despite an unmet outcome (`IssueStore.cs:2048`). Do not silently reinterpret Closed as delivered. Recommend a distinct deferred/abandoned disposition with an explicit sprint outcome of blocked/partial, or require operator-approved scope removal. Retain bounded triage and actionable failure visibility.

### S7 — Completion and follow-up materialization are not restart-safe as one operation

**High severity, high confidence from control flow.** Sprint completion commits before draft materialization (`SprintAssembler.cs:277`, `:294`). The materializer is entered only inside the branch for a formerly Active sprint. A process interruption after the status commit but before/during materialization leaves unconsumed drafts; the next tick has no Active sprint and skips that branch. Without another sprint completion, no automatic retry occurs. Task creation and draft disposition are also separate commits (`:658`, `:676`; `:716`, `:727`), so interrupted processing can duplicate tasks if the batch is later retried.

Recommend resumable, idempotent completion bookkeeping: scan completed sprints with outstanding drafts independently of whether a sprint just completed; use a stable draft/materialization identity and atomic or uniquely guarded create/consume. Keep this deterministic rather than adding a model-driven recovery agent.

### S8 — The operator proposal path bypasses automatic assembly policy

**High severity, high confidence.** The live composition wires `SprintProposeService` and `SprintProposeEndpoints` (`ForgeComposition.cs:282`, `Dashboard/DashboardHost.cs:573`). `ProposeAsync` scores all Pending rows; it does not use assembler grooming, container, ownership-theme, or dependency eligibility (`Orchestrator/Sprint/SprintProposeService.cs:57-80`). `CommitAsync` merely checks an audit row exists, creates an Active sprint, and attaches submitted IDs (`:102-130`). Endpoint validation checks task existence, not membership in the proposal or assembly policy (`Dashboard/SprintProposeEndpoints.cs:107-127`). Creating a new Active sprint archives the old Active sprint (`Core/SprintStore.cs:55`, `:250`).

The current Blazor Sprints page intentionally has no create button (`Forge.UI/Components/Pages/Sprints.razor:49`), and a source search found no current UI caller of these proposal endpoints. Treat this as a **registered API policy bypass**, not a claim that a visible button presently exposes it. The precise routes are `GET /api/sprints/propose-next` and `POST /api/sprints/propose-next/commit` (`Dashboard/SprintProposeEndpoints.cs:53`, `:84`).

This means a valid request can displace unfinished work and commit an ungroomed or arbitrary selection even while automatic assembly is held. The scoring path is not a more capable hidden planner: its theme match is a title substring, with priority/age bonuses and an in-other-sprint penalty (`Agents/DeterministicScorer.cs`). Recommend one shared preview/validate/activate service; the UI should explain or adjust a proposal, not invoke a competing policy.

## Recommended direction (for approval)

Choose a **bounded outcome planner with deterministic admission**, not a larger collection of special cases in `ResolveThemeKey`, and not an unconstrained model that chooses tasks and starts work itself.

1. Keep epics/specs as sources of requirements; introduce an explicit sprint objective and acceptance evidence. A spec can span several outcomes; an outcome can include tasks from several specs plus related backlog.
2. Build a candidate set from groomed work, carryover/rework, and explicit prerequisites. Separate admissibility from runnable-now status. Close over required dependencies; reject cycles or missing/failed prerequisites with a visible reason.
3. Rank candidate outcomes by operator priority and value, then bound scope using task/effort/cost and role-capacity estimates. These are planning limits; preserve the separate hard paid-request budget and execution slots.
4. Let a capable planning model propose a goal, task IDs, required prerequisites, evidence, and inclusion/exclusion reasons using a bounded snapshot. Validate deterministically that IDs exist, belong to the project, are groomed, are not owned by conflicting active work, and respect scope/dependency/budget limits. A small deterministic fallback may choose a known bounded ready outcome, but must not label a provenance group independently deployable without evidence.
5. Commit one durable plan and membership atomically after validation. Use the same implementation for automatic planning and the UI. Display goal, included/required/deferred work, expected cost, and why this plan wins.
6. During execution, automatically repair missing required prerequisites after grooming. Optional related improvements wait for the next plan unless the operator explicitly expands scope. A stalled task should not block unrelated projects; inside this project, a paused/partial outcome must be explicit rather than silently archived.
7. Complete only against the accepted outcome's evidence. Distinguish delivered, partial, blocked, and cancelled. Resume follow-up bookkeeping independently after crashes. Preserve reviewer findings and triage their relevance against the whole backlog, not only one source task.

This does **not** require replacing all grooming with another expensive layer. Existing technical grooming remains useful for feasibility, task shape, and relative priority (`Agents/GroomerAgent.cs:407`, `:433`, `:493`). The planner consumes that work; it should not repeatedly regroom every task or run on every hint. Replan only when no committed plan exists or a material change invalidates it, and cache the proposal against a versioned backlog snapshot.

Alternatives:

| Option | Benefit | Limitation |
|---|---|---|
| Patch grouping only (inherit epic root, include dependency closure, uniform cap) | Small, deterministic, quick | Still assumes ancestry equals a coherent outcome; won't reliably incorporate related independent backlog |
| Recommended bounded outcome planner + common activation validation | Solves the user's planning concern while preserving hard gates; readable proposal | Needs a goal/evidence contract and measured planning cost |
| General autonomous project-manager agent controlling sprints freely | Broad flexibility | Large audit/recovery surface; hard to explain scope changes; unnecessary for the first improvement |

Prioritize the concrete correctness gaps S3/S6/S7/S8 before enabling unattended planning. They are distinct from choosing the new product policy. Do not weaken plan, architecture, review, verification, or budget enforcement to improve throughput.

## Acceptance cases for the next implementation

- One outcome with A->B->C contains all required tasks; only dependency-ready tasks execute; sprint completion means C's acceptance evidence exists.
- Two follow-ups from different tasks and a related independent backlog item can join one objective with explicit rationale; unrelated work stays out despite spare slots.
- An oversized spec splits at meaningful outcome boundaries, not arbitrary ready waves or raw task count alone.
- A necessary parented blocker discovered mid-sprint is groomed and included or produces an explicit blocked-plan explanation; no silent deadlock.
- Unrelated ungroomed findings do not stall a ready independent plan. Required ungroomed work does prevent that plan from starting.
- Failed required work never becomes “delivered” solely through age. Deferral and scope reduction have visible reasons and preserved audit.
- Crash at each completion/materialization step causes neither lost drafts nor duplicate tasks; retry is deterministic and does not require another sprint to finish.
- Automatic and operator-triggered activation apply the same gate, eligibility, membership, and active-sprint checks. A stale proposal is revalidated; commit cannot silently archive in-flight work.
- Planning uses bounded context and bounded calls; proposals record estimated/actual planning cost and require no repeated calls when input has not changed.

## Validation record

Existing sprint assembler tests exercise actual themed packing, cap overflow, priority selection, grooming holds, failure completion, and injection (`tests/Forge.Tests/Integration/SprintAssemblerTests.cs:500`, `:530`, `:560`, `:725`, `:759`). Seven retained characterization fixtures in `tests/Forge.Tests/SprintPlanningAuditTests.cs` cover: dependency waves; 12-task spec membership; sibling-origin theme separation; global grooming hold; late parented prerequisite deadlock; interrupted completion leaving drafts; and proposal commit archiving unfinished active work while attaching an ungroomed task. They use real stores and no model calls. The interruption test injects a throwing triage seam after the completion commit; it proves bookkeeping is not retried on the next tick, not that the normal LLM client necessarily throws this exact exception.

Validation: `dotnet test tests/Forge.Tests/Forge.Tests.csproj --filter "FullyQualifiedName~SprintPlanningAuditTests|FullyQualifiedName~SprintAssemblerTests" --verbosity quiet` passed **45/45** (38 existing + 7 audit cases), Windows Debug, audit enabled, 2026-11-17. These fixtures assert observed current behavior, not approval of that behavior as the future policy. No production source, host, queue, or budget mutations were made by this audit. Shared budget-agent source edits were present during the final build but do not change the reviewed sprint code.
