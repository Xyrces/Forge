using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Core.Initiatives;
using Forge.Dashboard;
using Forge.Specs;

namespace Forge.Agents;

/// <summary>
/// P1.4 intake agent: a persistent conversation between the operator
/// and an LLM, scoped to a single project. The agent decides when to
/// propose an epic via the <see cref="CreateEpicTool"/> AIFunction; the
/// operator reviews proposed epics in the dashboard and either accepts
/// (which durably authorizes the exact revision for planning) or lets the LLM
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
    private readonly InitiativeApprovalService? _initiatives;
    private readonly string _projectId;
    private readonly IIntakeStore _intakeStore;
    private readonly IIssueStore _issues;
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
    private readonly string? _legacyVisionOwnerProjectId;
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
        Core.MemoryStore? memory = null,
        InitiativeApprovalService? initiatives = null,
        string? legacyVisionOwnerProjectId = null)
    {
        _initiatives = initiatives;
        _projectId = projectId;
        _intakeStore = intakeStore;
        _issues = issues;
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
        _legacyVisionOwnerProjectId = legacyVisionOwnerProjectId;
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
        string sessionId, string userText, CancellationToken ct = default, InitiativeProposal? workingBrief = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("userText is required", nameof(userText));

        var ownedSession = await _intakeStore.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException("Intake session not found.");
        if (ownedSession.ProjectId != _projectId)
            throw new InvalidOperationException("Intake session belongs to another project.");
        if (workingBrief is not null && (workingBrief.SessionId != sessionId || workingBrief.ProjectId != _projectId))
            throw new InvalidOperationException("Working brief belongs to another conversation.");

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
             [Description("Optional priority 1-5; defaults to 2.")] int? priority = null,
             [Description("Existing stable proposal ID when revising; omit for a new initiative.")] string? proposalId = null,
             [Description("Exact current revision when revising an existing proposal.")] int? expectedRevision = null) =>
                CreateEpicAsync(sessionId, title, description, priority, proposalId, expectedRevision, ct),
            name: "create_epic",
            description: "Propose a new epic for the operator to review. " +
                         "Use this for an operator's new initiative, including a small, clearly scoped change. " +
                         "The proposal is a durable unapproved draft in the working brief, linked to this conversation — " +
                         "no issue is created and nothing appears on the project board " +
                         "until the operator approves its exact revision. Returns a stable proposal ID " +
                         "and revision, not an issue ID. For changes, pass proposalId and expectedRevision.");

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

        // Discussion tools can only create/revise drafts or ask questions.
        // Approved specs belong to the downstream planning pipeline; Intake
        // must not silently change previously approved scope through tool calls.
        var tools = new List<AITool> { createEpicTool, askQuestionsTool };

        var instructions = await BuildIntakeInstructionsAsync(ct);
        if (workingBrief is not null)
        {
            var approvalState = workingBrief.ApprovedAt is null ? "draft" : "already approved";
            instructions += $"\n\nThe working brief shown with this message is proposal {workingBrief.Id}, "
                + $"revision {workingBrief.Revision} ({approvalState}). Resolve references like 'this brief' to that proposal.";
        }
        var agent = new ChatClientAgent(
            chatClient,
            instructions: instructions,
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
                // Provider doesn't do streaming — one buffered call
                // (through the pipeline runner so a leaked-markup
                // response gets nudged instead of ending the turn).
                var outcome = await new PipelineAgentRunner(_logger).RunAsync(
                    agent, history, roleLabel: "intake", ct: ct);
                assistantText = string.Concat(outcome.NewMessages
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
        // already wrote the DRAFT proposal message(s) (no issue row —
        // rows are created at accept time). Find the most recent
        // message carrying a proposal; the assistant message we are
        // about to append adopts that title so the thread audit card
        // can target it.
        var refreshed = await _intakeStore.GetAsync(sessionId, ct) ?? session;
        var lastProposal = refreshed.Messages
            .Where(m => m.ProposedEpicTitle is not null)
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
            else if (proposedEpicTitle is not null)
                assistantText = $"Proposed \"{proposedEpicTitle}\" — review the draft and accept when ready.";
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

    /// <summary>Legacy message ids are not immutable approval credentials.</summary>
    public Task<IssueRecord> AcceptProposedEpicAsync(string sessionId, long messageId, CancellationToken ct = default)
        => throw new InvalidOperationException("Review this draft as a durable proposal, then approve its exact current revision.");

    public async Task<InitiativeProposal> ImportLegacyDraftAsync(string sessionId, long messageId, CancellationToken ct = default)
    {
        var service = _initiatives ?? throw new InvalidOperationException("Durable initiative approval is unavailable.");
        var session = await _intakeStore.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException("Intake session not found.");
        if (session.ProjectId != _projectId) throw new InvalidOperationException("Session belongs to another project.");
        var message = session.Messages.SingleOrDefault(m => m.Id == messageId)
            ?? throw new InvalidOperationException("Draft message not found.");
        if (message.ProposedEpicDescription is null || message.ProposedEpicTitle is null)
            throw new InvalidOperationException("This message has no complete legacy draft payload.");
        if (message.ProposedEpicId is not null || session.Messages.Any(m =>
            m.Role == IntakeMessageRole.System && m.ProposedEpicId is not null &&
            m.Content.Contains($"(proposal message #{messageId})", StringComparison.Ordinal)))
            throw new InvalidOperationException("This legacy draft already links accepted work. Review the existing epic instead of importing it again.");
        return await service.Proposals.CreateAsync(new NewInitiativeProposal(_projectId,
            $"legacy:{sessionId}:{messageId}", "user", message.ProposedEpicTitle,
            message.ProposedEpicDescription, message.ProposedEpicPriority ?? 2,
            JsonSerializer.Serialize(new { sessionId, messageId }), sessionId), ct);
    }

    private async Task<string> CreateEpicAsync(string sessionId, string title, string description,
        int? priority, string? proposalId, int? expectedRevision, CancellationToken ct)
    {
        var service = _initiatives ?? throw new InvalidOperationException("Durable initiative proposals are unavailable.");
        var session = await _intakeStore.GetAsync(sessionId, ct)
            ?? throw new InvalidOperationException("Intake session not found.");
        if (session.ProjectId != _projectId) throw new InvalidOperationException("Session belongs to another project.");
        InitiativeProposal proposal;
        if (proposalId is not null)
        {
            var prior = await service.Proposals.GetAsync(_projectId, proposalId, ct)
                ?? throw new InvalidOperationException("Proposal not found.");
            if (prior.SessionId != sessionId || expectedRevision is null)
                throw new InvalidOperationException("Revision requires the proposal's linked session and exact current revision.");
            proposal = await service.Proposals.ReviseAsync(_projectId, proposalId, expectedRevision.Value,
                title, description, priority ?? 2, ct);
        }
        else
        {
            proposal = await service.Proposals.CreateAsync(new NewInitiativeProposal(_projectId,
                "intake:" + Guid.NewGuid().ToString("N"), "user", title, description, priority ?? 2,
                JsonSerializer.Serialize(new { sessionId }), sessionId), ct);
        }
        await _intakeStore.AppendMessageAsync(sessionId, new NewIntakeMessage(IntakeMessageRole.System,
            $"Proposal {proposal.Id} revision {proposal.Revision}: {proposal.Title}\n\n{proposal.Body}\n\nDraft only; explicit operator approval is required.",
            ProposedEpicTitle: proposal.Title), ct);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.epic.proposed", sessionId,
            proposal.Id, new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["proposalId"] = proposal.Id,
                ["revision"] = proposal.Revision
            }));
        return $"proposal {proposal.Id}, revision {proposal.Revision}; draft only, not approved";
    }

    private async Task<string> BuildIntakeInstructionsAsync(CancellationToken ct)
    {
        var roleInstructions = LoadRoleInstructions("intake");
        var projectLine = $"You are the Intake agent for project '{_projectId}'.";
        return $"""
            {projectLine}

            Your job: talk to the operator (the human) about what they want
            to build. Ask clarifying questions when intent or scope is unclear.
            New initiatives of any size enter through Intake, including a small,
            clearly scoped change. Call `create_epic(title, description, priority?)`
            to capture the reviewed scope as a durable draft proposal.
            To revise an existing draft, pass its stable proposalId and exact
            expectedRevision. Never use title similarity as proposal identity.
            Proposal creation and revision NEVER authorize execution; only
            the operator can approve the exact current revision in the UI.
            Do not require an artificial multi-task scope. Backlog task creation
            is for remediation or work already within an approved initiative;
            it is not a shortcut around approval for a new initiative.
            Capture the operator's intended outcome, acceptance criteria and
            boundaries in the proposal and confirm it remains unapproved.

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

            ## Conversation diagrams

            Diagrams can be opened alongside the working brief. When
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
    /// (exact vision/&lt;projectId&gt;, owner-bound legacy fallback). Missing
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
                vision = (await VisionMemory.ReadAsync(
                    _memory, _projectId, _legacyVisionOwnerProjectId, ct))?.Body;
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
