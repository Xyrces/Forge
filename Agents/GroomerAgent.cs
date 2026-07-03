using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// GroomerAgent: decomposes an Approved spec into 1-3 stories
/// (each 1-3 tasks) once the operator clicks "Start grooming"
/// from the Specs tab.
///
/// <para>
/// Per §6 of the workflow doc:
/// <list type="number">
///   <item>Read the Approved spec body (with acceptance criteria).</item>
///   <item>Plan 1-3 stories, each with 1-3 tasks sized for a single
///   engineering agent run.</item>
///   <item>Each task title comes from a spec acceptance criterion.</item>
///   <item>Create the stories + tasks (issues with type=story/task
///   and parent_id set).</item>
///   <item>Set the spec to status=Grooming once decomposition is done.</item>
/// </list>
/// </para>
///
/// <para>
/// Triggered by the operator via a "Start grooming" button on the
/// Specs tab (Phase 3 UI). Runs as a one-shot; the agent's tool
/// calls are what makes the decomposition visible.
/// </para>
/// </summary>
public sealed class GroomerAgent
{
    private readonly IIssueStore _issues;
    private readonly ISpecStore _specs;
    private readonly IDashboardEventBus _events;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly ILogger<GroomerAgent> _logger;
    private readonly string _runId;

    public GroomerAgent(
        IIssueStore issues,
        ISpecStore specs,
        IDashboardEventBus events,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        ILogger<GroomerAgent> logger,
        string? runId = null)
    {
        _issues = issues;
        _specs = specs;
        _events = events;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _logger = logger;
        _runId = runId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public string RunId => _runId;

    /// <summary>
    /// Groom a spec. Returns the list of new story issue ids, or
    /// null if the agent didn't call any create_story.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GroomAsync(string specId, CancellationToken ct = default)
    {
        var spec = await _specs.GetAsync(specId, ct);
        if (spec is null)
        {
            _logger.LogWarning("GroomerAgent.GroomAsync: spec {Id} not found", specId);
            return null;
        }
        // P2.a: the Groomer gate widens. Specs in Designed
        // (Designer approved) or Approved (operator-marked
        // non-visual) or Groomed (operator re-decompose) all
        // enter the Groomer. Specs in NeedsRevision / Draft /
        // ReadyForDesign / Shipped / Superseded / Archived are
        // rejected.
        if (spec.Status is not (SpecStatus.Designed
            or SpecStatus.AssetReady
            or SpecStatus.Approved
            or SpecStatus.Groomed))
        {
            _logger.LogWarning("GroomerAgent.GroomAsync: spec {Id} status={Status} (expected Designed | AssetReady | Approved | Groomed)",
                specId, spec.Status);
            return null;
        }

        var createdStoryIds = new List<string>();
        List<string> CreateStoryList() => createdStoryIds;

        var systemPrompt = $"""
            You are the GroomerAgent for project {spec.ProjectId}. Given an
            Approved spec, decompose it into 1-3 stories of 1-3 tasks each.

            Rules:
            - Each story is type=story, has parent_id=SPEC_ID, and is
              sized for a single engineering agent run (1-3 tasks).
            - Each task is type=task, has parent_id=STORY_ID, and
              has parent_id=SPEC_ID as well (we mirror the chain).
            - Each task title comes from a spec acceptance criterion.
            - Don't go over 1-3 stories, 1-3 tasks each.
            - When done, call `set_spec_status` with "Grooming".

            Use the `create_story` tool for each story and
            `create_task` for each task. You may call them in any
            order. After all stories + tasks are created, call
            `set_spec_status`.
            """;

        var chatClient = _chatClientFactory.Create(_config, AgentType.CoreDev);
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();

        var createStoryTool = AIFunctionFactory.Create(
            ([Description("Title of the story (human-readable, 1 line).")] string title,
             [Description("Optional 1-paragraph description.")] string? description = null) =>
                CreateStoryAsync(specId, title, description, CreateStoryList(), ct),
            name: "create_story",
            description: "Create a story issue linked to this spec. " +
                         "Returns the new story's issue id.");

        var createTaskTool = AIFunctionFactory.Create(
            ([Description("Title of the task (1 line, mirrors an acceptance criterion).")] string title,
             [Description("Story id this task belongs to.")] string storyId,
             [Description("Optional 1-paragraph description.")] string? description = null,
             [Description("Priority 1-5; defaults to 2.")] int? priority = null) =>
                CreateTaskAsync(specId, storyId, title, description, priority, createdStoryIds, ct),
            name: "create_task",
            description: "Create a task issue linked to the story AND the spec. " +
                         "Returns the new task's issue id.");

        var setStatusTool = AIFunctionFactory.Create(
            ([Description("Status: 'Draft', 'Approved', 'Superseded', 'Archived', or 'Grooming'.")] string status) =>
                SetStatusAsync(specId, status, ct),
            name: "set_spec_status",
            description: "Move the spec to a new status. Use 'Grooming' " +
                         "after all stories + tasks are created.");

        var tools = new List<AITool> { createStoryTool, createTaskTool, setStatusTool };
        var agent = new ChatClientAgent(
            chatClient,
            instructions: systemPrompt,
            name: "groomer",
            description: $"Groomer agent for spec {specId}",
            tools: tools);

        var userMessage = new ChatMessage(ChatRole.User, $"""
            Decompose this Approved spec into 1-3 stories + tasks.
            After creating the stories and tasks, call set_spec_status
            to "Grooming".

            Spec title: {spec.Title}
            Spec body:
            ```
            {spec.Body}
            ```
            """);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "groomer.run.started",
            specId, $"project={spec.ProjectId} runId={_runId}",
            new Dictionary<string, object?> { ["specId"] = specId, ["runId"] = _runId }));

        try
        {
            var response = await agent.RunAsync(userMessage, cancellationToken: ct);
            var refreshed = await _specs.GetAsync(specId, ct);
            _events.Publish(new DashboardEvent(DateTime.UtcNow,
                "groomer.run.completed", specId,
                $"status={refreshed?.Status} stories={createdStoryIds.Count}",
                new Dictionary<string, object?>
                {
                    ["specId"] = specId,
                    ["runId"] = _runId,
                    ["storyIds"] = string.Join(",", createdStoryIds),
                }));
            return createdStoryIds.Count == 0 ? null : createdStoryIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GroomerAgent failed for {Spec}", specId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, "groomer.run.failed",
                specId, ex.Message, new Dictionary<string, object?>
                {
                    ["specId"] = specId, ["runId"] = _runId, ["error"] = ex.Message,
                }));
            throw;
        }
    }

    private async Task<string> CreateStoryAsync(
        string specIdArg, string title, string? description,
        List<string> createdStoryIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return "title_required";
        var story = await _issues.CreateAsync(new NewIssue(
            Type: "story", Title: title, Description: description ?? "",
            ParentId: specIdArg, Priority: 2), ct);
        createdStoryIds.Add(story.Id);
        return story.Id;
    }

    private async Task<string> CreateTaskAsync(
        string specIdArg, string storyId, string title, string? description, int? priority,
        List<string> createdStoryIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return "title_required";
        if (string.IsNullOrWhiteSpace(storyId)) return "story_id_required";
        var task = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: title, Description: description ?? "",
            ParentId: storyId, Priority: priority ?? 2), ct);
        return task.Id;
    }

    private async Task<string> SetStatusAsync(
        string specIdArg, string status, CancellationToken ct)
    {
        if (!Enum.TryParse<SpecStatus>(status, ignoreCase: true, out var newStatus))
            return $"status_must_be_one_of_draft_approved_grooming_superseded_archived";
        try
        {
            var refreshed = await _specs.SetStatusAsync(specIdArg, newStatus, ct);
            return refreshed is null
                ? "spec_not_found"
                : $"status={refreshed.Status}";
        }
        catch (InvalidOperationException ex)
        {
            return $"error: {ex.Message}";
        }
    }
}