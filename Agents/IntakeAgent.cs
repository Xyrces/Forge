using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;
using Forge.Specs;

namespace Forge.Agents;

/// <summary>
/// P1.4 intake agent: a persistent conversation between the operator
/// and an LLM, scoped to a single project. The agent decides when to
/// propose an epic via the <see cref="CreateEpicTool"/> AIFunction; the
/// operator reviews proposed epics in the dashboard and either accepts
/// (which binds the issue to the active sprint) or lets the LLM
/// iterate.
///
/// <para>
/// The agent holds a <see cref="HarnessAgentSession"/> per project; the
/// session is a server-side MAF ChatClientAgent that the dashboard
/// streams tokens from. On operator message, the agent:
/// <list type="number">
///   <item>Appends the user message to the intake session.</item>
///   <item>Builds a ChatMessage history from the session's stored
///   messages (User/Assistant).</item>
///   <item>Calls ChatClientAgent with the full history (in-process
///   MAF, no HTTP).</item>
///   <item>Streams the response back via <see cref="IDashboardEventBus"/>
///   so the dashboard can show tokens as they arrive.</item>
///   <item>Appends the assistant message + any proposed epic IDs to
///   the intake session.</item>
/// </list>
/// </para>
///
/// <para>
/// One <see cref="IntakeAgent"/> instance is created per project and
/// lives for the lifetime of the orchestrator process. A crash or
/// process restart drops the in-memory cache; the
/// <see cref="IIntakeStore"/> persists the conversation, and the
/// next call to <see cref="SendUserMessageAsync"/> rebuilds the MAF
/// session from the persisted history.
/// </para>
/// </summary>
public sealed class IntakeAgent
{
    private readonly string _projectId;
    private readonly IIntakeStore _intakeStore;
    private readonly IIssueStore _issues;
    private readonly ISprintStore _sprints;
    private readonly ISpecStore? _specs;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly ISkillSource? _skills;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<IntakeAgent> _logger;
    private readonly string _rolePromptsRoot;
    private readonly string _defaultModel;
    private readonly Func<string, string?>? _projectRootLookup;
    private readonly Core.MemoryStore? _memory;
    private string? _repoBriefCache;
    private DateTime _repoBriefBuiltAt = DateTime.MinValue;
    private static readonly TimeSpan RepoBriefTtl = TimeSpan.FromMinutes(10);

    public IntakeAgent(
        string projectId,
        IIntakeStore intakeStore,
        IIssueStore issues,
        ISprintStore sprints,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ILogger<IntakeAgent> logger,
        ISpecStore? specs = null,
        ISkillSource? skills = null,
        string rolePromptsRoot = "agents",
        string defaultModel = "minimax-m2",
        Func<string, string?>? projectRootLookup = null,
        Core.MemoryStore? memory = null)
    {
        _projectId = projectId;
        _intakeStore = intakeStore;
        _issues = issues;
        _sprints = sprints;
        _specs = specs;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _logger = logger;
        _skills = skills;
        _rolePromptsRoot = rolePromptsRoot;
        _defaultModel = defaultModel;
        _projectRootLookup = projectRootLookup;
        _memory = memory;
    }

    public string ProjectId => _projectId;

    public async Task<IntakeSessionRecord> StartSessionAsync(string? title, CancellationToken ct = default)
        => await _intakeStore.CreateAsync(_projectId, title, ct);

    public async Task<IntakeSessionRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default)
        => await _intakeStore.GetAsync(sessionId, ct);

    public Task<IReadOnlyList<IntakeSessionRecord>> ListSessionsAsync(CancellationToken ct = default)
        => _intakeStore.ListAsync(ct);

    /// <summary>
    /// Send a user message to the agent. The agent runs the LLM, may
    /// call <see cref="CreateEpicTool"/> to propose an epic, and
    /// persists both the user message and the assistant response to
    /// the intake session.
    /// </summary>
    public async Task<IntakeSessionRecord> SendUserMessageAsync(
        string sessionId, string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("userText is required", nameof(userText));

        // Append the user message first so the message is durable even
        // if the LLM call throws. The agent response is appended after.
        var userMsg = await _intakeStore.AppendMessageAsync(sessionId,
            new NewIntakeMessage(IntakeMessageRole.User, userText), ct);

        var session = await _intakeStore.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Intake session {sessionId} not found");

        // Build the LLM-bound message list. The full history is sent so
        // the agent has conversational context across turns.
        var history = session.Messages
            .Select(m => new ChatMessage(MapRole(m.Role), m.Content))
            .ToList();

        var chatClient = _chatClientFactory.Create(_config, AgentType.Intake, _projectId);
        // Wrap the client with the function-invocation middleware so the
        // ChatClientAgent actually executes AIFunctions in its tool list
        // and feeds the result back to the LLM. Without this, the agent
        // gets a function-call response and just returns it as text.
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();

        // Build the AIFunction for create_epic and attach to the agent.
        // The tool captures the session id so the assistant message can
        // link to the proposed epic.
        var createEpicTool = AIFunctionFactory.Create(
            ([Description("Title of the proposed epic.")] string title,
             [Description("Long-form description of what the epic covers.")] string description,
             [Description("Optional priority 1-5; defaults to 2.")] int? priority) =>
                CreateEpicAsync(sessionId, title, description, priority, ct),
            name: "create_epic",
            description: "Propose a new epic for the operator to review. " +
                         "Use this when the operator's input describes a piece of work " +
                         "large enough to be a multi-task epic (vs. a single dev task). " +
                         "Returns the new epic's issue id.");

        // Structured clarifying questions (operator request 2026-08-12):
        // the tool captures the questions so the dashboard renders them
        // as a clickable form instead of a numbered list the operator
        // has to answer by retyping. When a model never calls the tool,
        // the fallback parser below lifts text questions into the same
        // shape.
        // NOTE: deliberately flat scalar args — a nested
        // array-of-objects parameter failed to bind on the live kimi
        // intake (2026-08-12: model retried with a stripped call and
        // the questions landed with zero options). Option descriptions
        // are em-dash encoded in the option string itself.
        var pendingQuestions = new List<IntakeQuestion>();
        var askQuestionsTool = AIFunctionFactory.Create(
            ([Description("Short label for the question, max 30 chars, e.g. 'Transport scope'.")] string header,
             [Description("The full question text.")] string question,
             [Description("2-5 options, each shaped 'Label — optional description'. Include for any choice-shaped question (yes/no, this-or-that, pick-list); omit only for genuinely free-form answers. Put the recommended option FIRST, prefixed '(Recommended)'.")] string[]? options = null,
             [Description("true when the operator may pick several options.")] bool multiple = false) =>
            {
                if (string.IsNullOrWhiteSpace(header))
                    return Task.FromResult("error: header is required (short label, max 30 chars)");
                if (header.Length > 30)
                    return Task.FromResult($"error: header exceeds 30 chars (got {header.Length})");
                if (string.IsNullOrWhiteSpace(question))
                    return Task.FromResult("error: question is required");
                if (question.Length > 240)
                    return Task.FromResult($"error: question exceeds 240 chars (got {question.Length})");
                if (options is { Length: 1 })
                    return Task.FromResult("error: options must be 2-5 entries shaped 'Label — description', or omit options entirely for a free-text answer");
                if (options is { Length: > 5 })
                    return Task.FromResult("error: options exceed maximum of 5");
                if (pendingQuestions.Count >= 8)
                    return Task.FromResult("error: question limit reached (8) — ask the rest in a later turn");
                var opts = (options ?? Array.Empty<string>())
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .ToArray();
                pendingQuestions.Add(new IntakeQuestion(question.Trim(), opts, header.Trim(), multiple));
                return Task.FromResult("ok");
            },
            name: "ask_question",
            description: "Ask the operator ONE clarifying question as a structured card with " +
                         "clickable options. Call once per question. ALWAYS use this instead of " +
                         "writing numbered questions as text when you need answers to proceed. " +
                         "The operator can pick options, multi-select when multiple=true, or " +
                         "answer any question free-form via its 'Other' input.");

        // Phase 2a tools: touches + add_dependency. Both require
        // SpecStore. We expose them as AIFunctions only when a
        // SpecStore was injected; otherwise the agent is told (via
        // its instructions) that these tools are unavailable.
        var tools = new List<AITool> { createEpicTool, askQuestionsTool };
        if (_specs is not null)
        {
            var activeSpecIdRef = sessionId;
            var touchesTool = AIFunctionFactory.Create(
                ([Description("Module or service this spec touches, e.g. 'PortHorizon.Core.Auth'.")] string moduleId,
                 [Description("Why this spec affects the module.")] string rationale) =>
                    TouchesAsync(activeSpecIdRef, moduleId, rationale, ct),
                name: "touches",
                description: "Declare that the current spec (the most recently proposed epic) " +
                             "touches a module or service. Use for each module the spec " +
                             "would change. Returns 'ok' on success.");
            tools.Add(touchesTool);

            var addDependencyTool = AIFunctionFactory.Create(
                ([Description("Target spec id, e.g. 'spec-abc123'.")] string targetSpecId,
                 [Description("Edge kind: 'blocks', 'depends_on', or 'related'.")] string kind,
                 [Description("One-line reason for the edge.")] string? rationale) =>
                    AddDependencyAsync(activeSpecIdRef, targetSpecId, kind, rationale, ct),
                name: "add_dependency",
                description: "Declare that the current spec has a dependency on another spec. " +
                             "Use 'blocks' if this spec must finish before target can start. " +
                             "Use 'depends_on' if this spec waits for target. " +
                             "Use 'related' for informational links. Returns 'ok' on success.");
            tools.Add(addDependencyTool);
        }

        var agent = new ChatClientAgent(
            chatClient,
            instructions: await BuildIntakeInstructionsAsync(ct),
            name: "intake",
            description: $"Intake agent for project {_projectId}",
            tools: tools);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.IntakeRunStarted,
            sessionId, $"project={_projectId}",
            new Dictionary<string, object?> { ["sessionId"] = sessionId, ["projectId"] = _projectId }));

        string assistantText;
        string? proposedEpicId = null;
        string? proposedEpicTitle = null;
        try
        {
            // Stream the run: every text delta is published to the
            // dashboard bus so the intake page renders a live bubble
            // instead of a dead spinner (operator request 2026-08-12:
            // "no feedback as to what is happening in the background
            // as the llm call is being made"). Function calls surface
            // as tool events so the operator sees "calling create_epic"
            // in place. DelegatingChatClient wrappers forward
            // GetStreamingResponseAsync; if a provider can't stream,
            // fall back to the buffered call.
            var sb = new StringBuilder();
            var announcedTools = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                await foreach (var update in agent.RunStreamingAsync(history, cancellationToken: ct))
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent call
                            && announcedTools.Add(call.Name))
                        {
                            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.IntakeRunTool,
                                sessionId, call.Name, new Dictionary<string, object?>
                                {
                                    ["sessionId"] = sessionId,
                                    ["tool"] = call.Name,
                                }));
                        }
                    }
                    var delta = update.Text;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        sb.Append(delta);
                        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.IntakeRunDelta,
                            sessionId, null, new Dictionary<string, object?>
                            {
                                ["sessionId"] = sessionId,
                                ["delta"] = delta,
                            }));
                    }
                }
                assistantText = sb.ToString();
            }
            catch (NotSupportedException)
            {
                // Provider doesn't do streaming — one buffered call.
                var response = await agent.RunAsync(history, cancellationToken: ct);
                assistantText = string.Concat(response.Messages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .Select(m => m.Text));
            }
        }
        catch (Exception ex)
        {
            // The user message is already persisted; we leave it as the
            // last entry. The next user message continues the thread.
            _logger.LogError(ex, "Intake agent run failed for session {Session}", sessionId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.IntakeRunFailed,
                sessionId, ex.Message, new Dictionary<string, object?> { ["error"] = ex.Message }));
            throw;
        }

        // If the agent called create_epic during the run, the tool
        // already wrote the issue and a system message linking to it.
        // Find the most recent message (any role) that carries a
        // proposedEpicId; the assistant message we are about to append
        // adopts that link so the dashboard's Accept button can target
        // it via the assistant message id.
        var refreshed = await _intakeStore.GetAsync(sessionId, ct) ?? session;
        var lastProposal = refreshed.Messages
            .Where(m => m.ProposedEpicId is not null)
            .OrderByDescending(m => m.Id)
            .FirstOrDefault();
        proposedEpicId = lastProposal?.ProposedEpicId;
        proposedEpicTitle = lastProposal?.ProposedEpicTitle;

        // Structured questions: prefer the ask_questions tool capture;
        // fall back to parsing numbered/bulleted questions out of the
        // reply text for models that ignore the tool.
        IReadOnlyList<IntakeQuestion>? questions = pendingQuestions.Count > 0
            ? pendingQuestions
            : IntakeQuestionParser.Parse(assistantText) is { Count: > 0 } parsed
                ? parsed
                : null;
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            // Tool-only replies have no text to persist; give the
            // assistant message a placeholder shaped by what the run
            // DID. A run with no text, no questions and no proposal
            // is a degenerate model response — persist a visible
            // fallback instead of throwing "content is required" out
            // of AppendMessageAsync (live 2026-08-14: the endpoint
            // 500'd and the operator lost the turn).
            if (questions is { Count: > 0 })
                assistantText = "A few questions before I proceed:";
            else if (proposedEpicId is not null)
                assistantText = $"Proposed {proposedEpicId} — review the draft and accept when ready.";
            else
            {
                _logger.LogWarning(
                    "Intake run for session {Session} produced no text, no tool calls and no questions",
                    sessionId);
                assistantText = "(The model returned an empty reply — please retry.)";
            }
        }

        // Append the assistant response (linked to the proposed epic if any).
        var assistantMsg = await _intakeStore.AppendMessageAsync(sessionId,
            new NewIntakeMessage(IntakeMessageRole.Assistant, assistantText,
                ProposedEpicId: proposedEpicId,
                ProposedEpicTitle: proposedEpicTitle,
                Questions: questions), ct);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.IntakeRunCompleted,
            sessionId, $"textLength={assistantText.Length}",
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["messageId"] = assistantMsg.Id,
                ["proposedEpicId"] = proposedEpicId,
            }));

        if (chatClient is IDisposable d) d.Dispose();

        return (await _intakeStore.GetAsync(sessionId, ct))!;
    }

    /// <summary>
    /// Mark a proposed epic as accepted by the operator. This:
    /// <list type="bullet">
    ///   <item>Transitions the issue from Pending -> keeps it Pending
    ///   (epics stay in the backlog until groomed; the operator's
    ///   "accept" means "I want this on the board" not "start working
    ///   on it now").</item>
    ///   <item>Adds the issue to the active sprint (if any).</item>
    ///   <item>Appends a system message to the intake session noting
    ///   the acceptance.</item>
    /// </list>
    /// </summary>
    public async Task<IssueRecord> AcceptProposedEpicAsync(
        string sessionId, long messageId, CancellationToken ct = default)
    {
        var session = await _intakeStore.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Intake session {sessionId} not found");
        var msg = session.Messages.FirstOrDefault(m => m.Id == messageId)
            ?? throw new InvalidOperationException($"Message {messageId} not found in session {sessionId}");
        if (msg.ProposedEpicId is null)
            throw new InvalidOperationException(
                $"Message {messageId} did not propose an epic; nothing to accept.");

        var issue = await _issues.GetAsync(msg.ProposedEpicId, ct)
            ?? throw new InvalidOperationException($"Proposed epic {msg.ProposedEpicId} no longer exists");

        // P2.a wiring: create a spec for the accepted epic so the
        // product -> designer pipeline has something to refine.
        // The spec's parent_issue_id is the epic; the product
        // refinement queue's "intake.epic.accepted" listener picks
        // up this event, looks up the spec by parent_issue_id, and
        // refines the body. We seed the spec body with the intake
        // draft so Product has something to work with even before
        // the LLM runs.
        SpecRecord? specForEpic = null;
        if (_specs is not null)
        {
            // Project-SCOPED probe: issue ids are per-store sequences
            // (every project has an epic-2), so a fan-out lookup by
            // parent_issue_id can match ANOTHER project's spec and
            // silently skip creating this one (observed live
            // 2026-08-09: talaria's accepted epic-2 found porthorizon's
            // Epic-B spec via the collision and no talaria spec was
            // ever created — the project never entered the pipeline).
            var existing = await _specs.ListAsync(projectId: _projectId, status: null, ct);
            specForEpic = existing.FirstOrDefault(s => s.ParentIssueId == issue.Id);
            if (specForEpic is null)
            {
                specForEpic = await _specs.CreateAsync(new NewSpec(
                    ProjectId: _projectId,
                    Title: issue.Title,
                    Body: BuildIntakeDraftBody(issue),
                    Author: "intake",
                    ParentIssueId: issue.Id));
            }
        }

        // Bind to active sprint (if any).
        var activeSprint = await _sprints.GetActiveAsync(ct);
        if (activeSprint is not null)
        {
            try { await _sprints.AddIssueAsync(activeSprint.Id, issue.Id, ct); }
            catch (InvalidOperationException)
            {
                // Already in the sprint; idempotent accept.
            }
        }

        // Append a system message to the session so the audit trail is
        // visible in the chat thread.
        var sprintNote = activeSprint is null
            ? "no active sprint"
            : $"added to sprint {activeSprint.Id} ({activeSprint.Name})";
        await _intakeStore.AppendMessageAsync(sessionId,
            new NewIntakeMessage(IntakeMessageRole.System,
                $"Operator accepted epic {issue.Id}: {issue.Title}; {sprintNote}."), ct);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.epic.accepted",
            sessionId, $"epic={issue.Id}", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["epicId"] = issue.Id,
                // Issue ids are per-store sequences — the refinement
                // queue MUST scope its spec lookup by project or an
                // epic-N collision routes it to another project's spec
                // (observed live 2026-08-09: talaria epic-2 → refined
                // porthorizon's Epic-B spec).
                ["projectId"] = _projectId,
                ["sprintId"] = activeSprint?.Id,
            }));

        return issue;
    }

    private async Task<string> CreateEpicAsync(
        string sessionId, string title, string description, int? priority, CancellationToken ct)
    {
        // One ACTIVE proposal per TITLE: re-proposing the SAME epic
        // (retry loops, per-turn re-proposals, "(refined)" suffixes)
        // refines that epic in place — without a collapse each call
        // spawned a duplicate epic row (observed live 2026-08-12: one
        // talaria turn created epic-5/6/7 with identical titles; two
        // of the five ASB epics were then accepted → duplicate specs
        // in the pipeline). A clearly DIFFERENT title is a genuinely
        // new epic, even while an earlier proposal is still
        // unaccepted — a parent + children turn must be able to create
        // them all (observed live 2026-08-14: the collapse rewrote
        // epic-8 four times and the five children were never created).
        // Only an ACCEPTED proposal closes the session's proposal
        // slot either way.
        var session = await _intakeStore.GetAsync(sessionId, ct);
        var lastProposalId = session?.Messages
            .Where(m => m.ProposedEpicId is not null)
            .OrderByDescending(m => m.Id)
            .FirstOrDefault()?.ProposedEpicId;
        if (lastProposalId is not null)
        {
            var accepted = session!.Messages.Any(m =>
                m.Role == IntakeMessageRole.System
                && m.Content.StartsWith($"Operator accepted epic {lastProposalId}:", StringComparison.Ordinal));
            if (!accepted)
            {
                var existing = await _issues.GetAsync(lastProposalId, ct);
                if (existing is not null && IsSameProposal(existing.Title, title))
                {
                    await _issues.UpdateSummaryAsync(existing.Id, title, description, ct);
                    if (priority is not null)
                        await _issues.SetPriorityAsync(existing.Id, priority.Value, ct);
                    await _intakeStore.AppendMessageAsync(sessionId,
                        new NewIntakeMessage(IntakeMessageRole.System,
                            $"Updated epic proposal: {existing.Id} - {title}",
                            ProposedEpicId: existing.Id, ProposedEpicTitle: title), ct);
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.epic.proposed",
                        sessionId, $"epic={existing.Id} (revised)", new Dictionary<string, object?>
                        {
                            ["sessionId"] = sessionId,
                            ["epicId"] = existing.Id,
                        }));
                    return existing.Id;
                }
            }
        }

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: "epic",
            Title: title,
            Description: description,
            Priority: priority ?? 2,
            Assignee: "intake"), ct);

        // Append a system message that links the proposed epic id+title
        // to the next assistant message. The runner reads
        // `LastAssistant.ProposedEpicId` after the run and threads it
        // into the assistant's stored record.
        await _intakeStore.AppendMessageAsync(sessionId,
            new NewIntakeMessage(IntakeMessageRole.System,
                $"Proposed epic: {issue.Id} - {issue.Title}", ProposedEpicId: issue.Id, ProposedEpicTitle: title), ct);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.epic.proposed",
            sessionId, $"epic={issue.Id}", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["epicId"] = issue.Id,
            }));

        return issue.Id;
    }

    /// <summary>Title similarity for the refine-vs-create decision:
    /// normalized (case/punctuation/parenthesized-qualifiers stripped)
    /// titles count as the same proposal when equal or one contains
    /// the other as a prefix — "ASB transport" vs
    /// "ASB transport (refined)" refine, "Parent epic" vs
    /// "P1-1: Fix the test harness" create.</summary>
    private static bool IsSameProposal(string existingTitle, string newTitle)
    {
        var a = NormalizeProposalTitle(existingTitle);
        var b = NormalizeProposalTitle(newTitle);
        if (a.Length == 0 || b.Length == 0) return true;
        return a == b
            || a.StartsWith(b, StringComparison.Ordinal)
            || b.StartsWith(a, StringComparison.Ordinal);
    }

    private static string NormalizeProposalTitle(string title)
    {
        var noParens = System.Text.RegularExpressions.Regex.Replace(title, @"\([^)]*\)", " ");
        var lowered = noParens.ToLowerInvariant();
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return System.Text.RegularExpressions.Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Look up the spec most recently proposed by this session.
    /// Used by the touches/add_dependency tools to find which spec
    /// to attribute the call to.
    /// </summary>
    private async Task<SpecRecord?> MostRecentSpecAsync(string sessionId, CancellationToken ct)
    {
        var session = await _intakeStore.GetAsync(sessionId, ct);
        if (session is null) return null;
        // The last system message with a ProposedEpicId is the most recent
        // proposed spec. (SpecStore has it by id, but we recorded the
        // issue id at propose-time, not the spec id. Re-link via issue.)
        var lastProposal = session.Messages
            .Where(m => m.ProposedEpicId is not null)
            .OrderByDescending(m => m.Id)
            .FirstOrDefault();
        if (lastProposal?.ProposedEpicId is null) return null;
        // The spec was created from the same epic; find the spec whose
        // parent_issue_id matches.
        var issue = await _issues.GetAsync(lastProposal.ProposedEpicId, ct);
        if (issue is null) return null;
        var specs = await _specs!.ListAsync(_projectId, status: null, ct);
        return specs.FirstOrDefault(s => s.ParentIssueId == issue.Id);
    }

    private async Task<string> TouchesAsync(
        string sessionId, string moduleId, string rationale, CancellationToken ct)
    {
        if (_specs is null) return "spec_store_unavailable";
        if (string.IsNullOrWhiteSpace(moduleId))
            return "module_id_required";
        var spec = await MostRecentSpecAsync(sessionId, ct);
        if (spec is null) return "no_recent_spec";
        // Read existing touches (declare or auto) and add this one.
        // SpecStore.PersistExtractionAsync will overwrite on next body
        // update; for runtime additions we need a separate path. To
        // keep this scope limited, we append a section to the spec
        // body via UpdateBodyAsync and let the extractor pick it up.
        // That's "extra work" but stays consistent with the source-
        // of-truth-is-body contract.
        await _specs.UpdateBodyAsync(spec.Id,
            new UpdateSpecBody(spec.Body + $"\n\n## Touches\n- {moduleId}{(string.IsNullOrWhiteSpace(rationale) ? "" : "  \n    - " + rationale)}\n", spec.Author),
            ct);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.spec.touched",
            sessionId, $"spec={spec.Id} module={moduleId}",
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["specId"] = spec.Id,
                ["moduleId"] = moduleId,
            }));
        return "ok";
    }

    private async Task<string> AddDependencyAsync(
        string sessionId, string targetSpecId, string kind, string? rationale, CancellationToken ct)
    {
        if (_specs is null) return "spec_store_unavailable";
        if (string.IsNullOrWhiteSpace(targetSpecId)) return "target_spec_required";
        if (kind is not ("blocks" or "depends_on" or "related"))
            return "kind_must_be_blocks_depends_on_or_related";
        var spec = await MostRecentSpecAsync(sessionId, ct);
        if (spec is null) return "no_recent_spec";
        var targetExists = await _specs.GetAsync(targetSpecId, ct);
        if (targetExists is null) return "target_spec_not_found";
        // Same pattern as TouchesAsync: append a section to the body
        // and let the extractor populate spec_dep.
        var newSection = $"## Dependencies\n- {kind} {targetSpecId}{(string.IsNullOrWhiteSpace(rationale) ? "" : " — " + rationale)}\n";
        var newBody = AppendSectionIfMissing(spec.Body, "Dependencies", newSection.TrimEnd('\n'));
        await _specs.UpdateBodyAsync(spec.Id, new UpdateSpecBody(newBody, spec.Author), ct);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.spec.dep_added",
            sessionId, $"from={spec.Id} to={targetSpecId} kind={kind}",
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["fromSpecId"] = spec.Id,
                ["toSpecId"] = targetSpecId,
                ["kind"] = kind,
            }));
        return "ok";
    }

    /// <summary>
    /// Append a section to the body if it isn't already present.
    /// If present, append an extra bullet to the existing section
    /// rather than duplicating the heading.
    /// </summary>
    private static string AppendSectionIfMissing(string body, string headingName, string fullSection)
    {
        var marker = "## " + headingName;
        if (body.Contains(marker, StringComparison.Ordinal))
            return body.TrimEnd() + "\n\n" + fullSection.Substring(fullSection.IndexOf('\n') + 1);
        return body.TrimEnd() + "\n\n" + fullSection;
    }

    private async Task<string> BuildIntakeInstructionsAsync(CancellationToken ct)
    {
        var roleInstructions = LoadRoleInstructions("intake");
        var projectLine = $"You are the Intake agent for project '{_projectId}'.";
        return $"""
            {projectLine}

            Your job: talk to the operator (the human) about what they want
            to build. Ask clarifying questions. When the operator describes
            a piece of work that is large enough to be a multi-task epic,
            call `create_epic(title, description, priority?)` to propose it.
            Do NOT propose epics for single tasks — those are dev tasks and
            don't go through intake.

            If the operator is being vague, ask. If they describe a single
            task, suggest they file a dev task instead of an epic. If they
            describe an epic, capture the title + description in the tool
            call and confirm the proposal in your reply.

            Sequence matters: when you have clarifying questions, call
            `ask_question` and STOP — never call `create_epic` in the
            same reply. The epic proposal belongs in a LATER turn, once
            the operator's answers are in, so the title/description
            reflect the discussed scope. Proposing before the answers
            are in presents the operator an Accept button for a spec
            you already know is incomplete.

            When you need answers from the operator, call
            `ask_question(header, question, options, multiple)` once per
            question — the dashboard renders them as a clickable form.
            Keep a SHORT text summary in your reply too (one line per
            question at most); never dump a long numbered question list
            as text when the tool covers it. Options are 2-5 strings
            shaped 'Label — description'; put the recommended option
            FIRST, prefixed '(Recommended)'. Omit options only for
            genuinely free-form answers.

            Do NOT ask the operator about facts the project brief below
            already answers (tech stack, layout, existing modules). Read
            it first; ask only about intent and scope.

            ## Visual canvas

            The operator watches a visual canvas beside this chat. When
            the discussion turns to structure — modules, flows, data
            flow, component relationships, user journeys — include ONE
            small mermaid diagram in your reply as a fenced block:

            ```mermaid
            flowchart LR
              A[Short label] --> B[Short label]
            ```

            Rules: flowchart or sequenceDiagram only; at most ~12 nodes;
            short labels in brackets; no raw HTML, no images, no
            click/link directives. UPDATE the diagram as the design
            evolves (emit the revised diagram in a later reply) instead
            of piling up near-duplicates. Skip the diagram for small
            talk and clarifying questions.

            ## Project grounding

            {await BuildGroundingAsync(ct)}

            {roleInstructions}
            """;
    }

    /// <summary>
    /// What the project IS: the operator's vision memory (when one
    /// exists) + a brief built from the local clone (stack, layout,
    /// README). Same vision keys as the groomer
    /// (vision/&lt;projectId&gt;, fallback vision/master). Missing
    /// pieces degrade to explicit markers, never silence — a new
    /// project with no clone yet must not hallucinate a stack
    /// (observed live 2026-08-09: the talaria intake asked the
    /// operator what the tech stack was).
    /// </summary>
    private async Task<string> BuildGroundingAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? vision = null;
        if (_memory is not null)
        {
            try
            {
                vision = (await _memory.RecallAsync($"vision/{_projectId}", ct)).FirstOrDefault()?.Body;
                if (string.IsNullOrWhiteSpace(vision))
                    vision = (await _memory.RecallAsync("vision/master", ct)).FirstOrDefault()?.Body;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake: vision recall failed for {ProjectId}; continuing without", _projectId);
            }
        }
        if (!string.IsNullOrWhiteSpace(vision))
        {
            sb.AppendLine("### Project vision");
            sb.AppendLine(vision.Length > 4000 ? vision[..4000] + "\n...[truncated]..." : vision);
            sb.AppendLine();
        }

        sb.AppendLine("### Project brief (from the local clone)");
        sb.AppendLine(RepoBrief());
        return sb.ToString();
    }

    private string RepoBrief()
    {
        if (_repoBriefCache is not null
            && DateTime.UtcNow - _repoBriefBuiltAt < RepoBriefTtl)
        {
            return _repoBriefCache;
        }
        string? root = null;
        if (_projectRootLookup is not null)
        {
            try { root = _projectRootLookup(_projectId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake: project root lookup failed for {ProjectId}", _projectId);
            }
        }
        _repoBriefCache = ProjectRepoBrief.Build(root);
        _repoBriefBuiltAt = DateTime.UtcNow;
        return _repoBriefCache;
    }

    private string LoadRoleInstructions(string agentName)
    {
        var path = Path.Combine(_rolePromptsRoot, agentName + ".md");
        if (!File.Exists(path)) return string.Empty;
        return File.ReadAllText(path);
    }

    private static ChatRole MapRole(IntakeMessageRole role) => role switch
    {
        IntakeMessageRole.User => ChatRole.User,
        IntakeMessageRole.Assistant => ChatRole.Assistant,
        IntakeMessageRole.System => ChatRole.System,
        _ => ChatRole.User,
    };

    /// <summary>
    /// The intake-draft body used when a new spec is created from
    /// an accepted epic. The Product agent refines this into the
    /// structured form (## Summary, ## Acceptance criteria, etc).
    /// We seed it with the issue's title + description so Product
    /// has material to work with even before the LLM runs.
    /// </summary>
    private static string BuildIntakeDraftBody(IssueRecord issue)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Summary\n").Append(issue.Title).Append('\n');
        if (!string.IsNullOrWhiteSpace(issue.Description))
        {
            sb.Append("\n## Notes\n").Append(issue.Description).Append('\n');
        }
        sb.Append("\n## Touches\n- TBD\n");
        sb.Append("\n## Dependencies\n- none\n");
        sb.Append("\n## Out of scope\n- TBD\n");
        sb.Append("\n## Open questions\n- TBD\n");
        return sb.ToString();
    }
}

/// <summary>
/// Registry of <see cref="IntakeAgent"/> instances, one per project.
/// Created once at orchestrator startup; the dashboard looks up an
/// agent by projectId for each API call.
/// </summary>
public sealed class IntakeAgentRegistry
{
    private readonly ConcurrentDictionary<string, IntakeAgent> _byProject = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, IntakeAgent> _factory;

    public IntakeAgentRegistry(Func<string, IntakeAgent> factory)
    {
        _factory = factory;
    }

    public IntakeAgent ForProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        return _byProject.GetOrAdd(projectId, _factory);
    }

    public bool ContainsProject(string projectId)
        => _byProject.ContainsKey(projectId);
}
