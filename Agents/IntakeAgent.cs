using System.Collections.Concurrent;
using System.ComponentModel;
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
        ISpecStore? specs = null,
        ISkillSource? skills = null,
        string kiloAgentsRoot = ".kilo/agents",
        string defaultModel = "minimax-m2")
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

        // Phase 2a tools: touches + add_dependency. Both require
        // SpecStore. We expose them as AIFunctions only when a
        // SpecStore was injected; otherwise the agent is told (via
        // its instructions) that these tools are unavailable.
        var tools = new List<AITool> { createEpicTool };
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
            instructions: BuildIntakeInstructions(),
            name: "intake",
            description: $"Intake agent for project {_projectId}",
            tools: tools);

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
            var existing = await _specs.ListAsync(projectId: null, status: null, ct);
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
