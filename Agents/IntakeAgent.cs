using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;

namespace PortHorizon.Agents.Agents;

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
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly ISkillSource? _skills;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<IntakeAgent> _logger;
    private readonly string _kiloAgentsRoot;
    private readonly string _defaultModel;

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
        ISkillSource? skills = null,
        string kiloAgentsRoot = ".kilo/agents",
        string defaultModel = "minimax-m2")
    {
        _projectId = projectId;
        _intakeStore = intakeStore;
        _issues = issues;
        _sprints = sprints;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _logger = logger;
        _skills = skills;
        _kiloAgentsRoot = kiloAgentsRoot;
        _defaultModel = defaultModel;
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

        var chatClient = _chatClientFactory.Create(_config, AgentType.Intake);
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

        var agent = new ChatClientAgent(
            chatClient,
            instructions: BuildIntakeInstructions(),
            name: "intake",
            description: $"Intake agent for project {_projectId}",
            tools: new[] { createEpicTool });

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.run.started",
            sessionId, $"project={_projectId}",
            new Dictionary<string, object?> { ["sessionId"] = sessionId, ["projectId"] = _projectId }));

        string assistantText;
        string? proposedEpicId = null;
        string? proposedEpicTitle = null;
        try
        {
            var response = await agent.RunAsync(history, cancellationToken: ct);
            assistantText = string.Concat(response.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text));
        }
        catch (Exception ex)
        {
            // The user message is already persisted; we leave it as the
            // last entry. The next user message continues the thread.
            _logger.LogError(ex, "Intake agent run failed for session {Session}", sessionId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.run.failed",
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

        // Append the assistant response (linked to the proposed epic if any).
        var assistantMsg = await _intakeStore.AppendMessageAsync(sessionId,
            new NewIntakeMessage(IntakeMessageRole.Assistant, assistantText,
                ProposedEpicId: proposedEpicId,
                ProposedEpicTitle: proposedEpicTitle), ct);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "intake.run.completed",
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
                ["sprintId"] = activeSprint?.Id,
            }));

        return issue;
    }

    private async Task<string> CreateEpicAsync(
        string sessionId, string title, string description, int? priority, CancellationToken ct)
    {
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

    private string BuildIntakeInstructions()
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

            {roleInstructions}
            """;
    }

    private string LoadRoleInstructions(string kiloAgentName)
    {
        var path = Path.Combine(_kiloAgentsRoot, kiloAgentName + ".md");
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
