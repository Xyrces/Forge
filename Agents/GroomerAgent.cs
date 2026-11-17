using System.ComponentModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Agents;

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
    private readonly MemoryStore? _memory;
    private readonly string? _projectRoot;
    private readonly string? _projectId;
    private readonly Func<string, string?>? _projectRootLookup;
    private readonly Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? _roleCapacitiesLookup;
    private readonly Func<string, CancellationToken, Task<IReadOnlyDictionary<string, RoleTerritory>>>? _roleTerritoriesLookup;
    private readonly string _runId;

    public GroomerAgent(
        IIssueStore issues,
        ISpecStore specs,
        IDashboardEventBus events,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        ILogger<GroomerAgent> logger,
        string? runId = null,
        MemoryStore? memory = null,
        string? projectRoot = null,
        string? projectId = null,
        Func<string, string?>? projectRootLookup = null,
        Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? roleCapacitiesLookup = null,
        Func<string, CancellationToken, Task<IReadOnlyDictionary<string, RoleTerritory>>>? roleTerritoriesLookup = null)
    {
        _issues = issues;
        _specs = specs;
        _events = events;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _logger = logger;
        _memory = memory;
        _projectRoot = projectRoot;
        _projectId = projectId;
        _projectRootLookup = projectRootLookup;
        _roleCapacitiesLookup = roleCapacitiesLookup;
        _roleTerritoriesLookup = roleTerritoriesLookup;
        _runId = runId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public string RunId => _runId;

    /// <summary>
    /// Groom a spec. Returns the list of new story issue ids, or
    /// null if the agent didn't call any create_story.
    /// </summary>
    public async Task<GroomResult?> GroomAsync(string specId, CancellationToken ct = default, bool resumeExisting = false)
    {
        var spec = await _specs.GetAsync(specId, ct);
        if (spec is null)
        {
            _logger.LogWarning("GroomerAgent.GroomAsync: spec {Id} not found", specId);
            return null;
        }
        // Visual work must finish the artist stage first. Approved is
        // the explicit nonvisual bypass; Groomed is an operator re-decomposition.
        if (!SpecActions.CanStartGrooming(spec.Status) && spec.Status != SpecStatus.Groomed
            && !(resumeExisting && spec.Status == SpecStatus.Grooming))
        {
            _logger.LogWarning("GroomerAgent.GroomAsync: spec {Id} status={Status} (expected AssetReady | Approved | Groomed)",
                specId, spec.Status);
            return null;
        }

        // Hide all tasks, including an explicit re-decomposition, until
        // the run has finished creating tasks and dependency edges.
        if (spec.Status == SpecStatus.Groomed)
            await _specs.SetStatusAsync(specId, SpecStatus.Approved, ct);
        await _specs.SetStatusAsync(specId, SpecStatus.Grooming, ct);

        var createdStoryIds = new List<string>();
        var createdTaskIds = new List<string>();
        var taskCountByStory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<IssueRecord> existing = [];
        if (spec.Status != SpecStatus.Groomed)
        {
            var owner = _issues as IssueStore ?? throw new InvalidOperationException("Grooming requires the owning project store.");
            existing = await new GroomingDecompositionStore(owner, _specs).ReadPendingAsync(specId, ct);
            createdStoryIds.AddRange(existing.Where(r => r.Type == "story").Select(r => r.Id));
            createdTaskIds.AddRange(existing.Where(r => r.Type != "story").Select(r => r.Id));
            foreach (var storyId in createdStoryIds)
                taskCountByStory[storyId] = existing.Count(r => r.ParentIssueId == storyId);
        }
        var reusedStories = createdStoryIds.Count;
        var reusedTasks = createdTaskIds.Count;
        List<string> CreateStoryList() => createdStoryIds;

        var grounding = await BuildGroundingBlockAsync(spec.ProjectId, ct);
        var runProjectId = spec.ProjectId;

        var systemPrompt = $"""
            You are the GroomerAgent for project {spec.ProjectId}. Given an
            Approved spec, decompose it into 1-3 stories of 1-3 tasks each.

            Rules:
            - VERIFY AGAINST THE VISION first: every story you create
              must serve the project vision below. If the spec (or part
              of it) contradicts or lies outside the vision, do not
              invent stories for that part — note it in your reply.
            - PLAN AGAINST CURRENT STATE: the open-work digest below
              is what is already planned or in flight. Do not re-plan
              work that already exists; build on it.
            - Each story is type=story, has parent_id=SPEC_ID, and is
              sized for a single engineering agent run (1-3 tasks).
            - Each task is type=task, has parent_id=STORY_ID, and
              has parent_id=SPEC_ID as well (we mirror the chain).
            - Each task title comes from a spec acceptance criterion.
            - Don't go over 1-3 stories, 1-3 tasks each. The tools
              enforce these caps and return limit errors.
            - WIRE PHYSICAL PREREQUISITES with `add_dependency`: when
              task B edits a project/file/module that task A creates,
              or needs infrastructure A lands first, call
              add_dependency(blocker_id: A, blocked_id: B) so B stays
              out of the dispatch queue until A merges. Example: the
              task creating a new csproj blocks every task writing
              classes into it. Don't serialize soft preferences —
              disjoint files run in parallel.
            - When done, call `set_spec_status` with "Groomed".

            Use the `create_story` tool for each story and
            `create_task` for each task. You may call them in any
            order. After all stories + tasks are created, wire any
            blocks edges, then call `set_spec_status`.

            {grounding}
            """;

        var chatClient = _chatClientFactory.Create(_config, AgentType.Groomer, runProjectId);
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
             [Description("Priority 1-5; defaults to 2.")] int? priority = null,
             [Description("Task type — routes dispatch to the right role. 'task' (default) = coredev (Core/sim/tests/docs). 'client' (or 'ui'/'godot') = clientdev (Client/Scripts, scenes, UI work). 'qa' (or 'playtest'/'test') = the QA role (playthroughs, evidence capture). Set it when the work is obviously one of those — a mistyped task dies at the plan-territory gate.")] string? taskType = null) =>
                CreateTaskAsync(specId, storyId, title, description, priority, taskType, createdStoryIds, createdTaskIds, taskCountByStory, ct),
            name: "create_task",
            description: "Create a task issue linked to the story AND the spec. " +
                         "Returns the new task's issue id.");

        var completionRequested = false;
        var setStatusTool = AIFunctionFactory.Create(
            ([Description("Status: 'Draft', 'Approved', 'Groomed', 'Superseded', or 'Archived'.")] string status) =>
            {
                if (string.Equals(status, "Groomed", StringComparison.OrdinalIgnoreCase))
                {
                    completionRequested = true;
                    return Task.FromResult("Groomed requested; publication waits until this run finishes.");
                }
                return SetStatusAsync(specId, status, ct);
            },
            name: "set_spec_status",
            description: "Move the spec to a new status. Use 'Groomed' " +
                         "after all stories + tasks are created.");

        // Physical-prerequisite wiring: without blocks edges the whole
        // decomposed sprint dispatches in parallel and dependents start
        // before their scaffold merges (observed live 2026-08-12:
        // talaria Sprint 7 — the new-project task ran concurrently
        // with the tasks writing classes into that project).
        var addDependencyTool = AIFunctionFactory.Create(
            ([Description("The task that must merge FIRST (the prerequisite).")] string blockerId,
             [Description("The task that cannot start until the blocker merges.")] string blockedId,
             [Description("One-line reason (what the blocker provides).")] string? rationale = null) =>
                AddTaskDependencyAsync(blockerId, blockedId, rationale, ct),
            name: "add_dependency",
            description: "Declare that a task CANNOT START until another task merges " +
                         "(a physical prerequisite: it creates the project/file/infrastructure " +
                         "the other task edits). Not for soft ordering — disjoint work stays " +
                         "parallel. Returns 'ok' or an error string.");

        var bindTool = CreateOutcomeBindingTool(id => createdTaskIds.Contains(id), ct);
        var scopeTool = CreateApprovedScopeTool(ct);
        systemPrompt += OutcomeInstructions;
        systemPrompt += "\nApproved scope lookup: read_approved_scope with specId=" + specId;
        systemPrompt += "\nPreserved decomposition is authoritative: reuse these exact Pending IDs, bind any missing outcomes, and create only genuinely missing work. Do not repeat decomposition or replace existing descriptions. Exact repeated create calls return the existing row.\n"
            + System.Text.Json.JsonSerializer.Serialize(existing.Select(r => new { r.Id, r.Type, r.Title, r.ParentIssueId,
                description = r.Description is { Length: > 2000 } text ? text[..2000] : r.Description,
                hasOutcomeMapping = SprintPlanJson.ReadBinding(r) is not null }));
        var tools = new List<AITool> { createStoryTool, createTaskTool, addDependencyTool, bindTool, scopeTool, CreateOutcomeCatalogTool(ct), CreateOutcomeReadTool(ct), setStatusTool };
        var agent = new ChatClientAgent(
            chatClient,
            instructions: systemPrompt,
            name: "groomer",
            description: $"Groomer agent for spec {specId}",
            tools: tools);

        var userMessage = new ChatMessage(ChatRole.User, $"""
            Decompose this Approved spec into 1-3 stories + tasks.
            After creating the stories and tasks, call set_spec_status
            to "Groomed".

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
            var outcome = await new PipelineAgentRunner(_logger).RunAsync(
                agent, new[] { userMessage }, roleLabel: "groomer",
                requiredToolName: "set_spec_status",
                contractNudgePrompt: "Finish by calling set_spec_status: Groomed after decomposition, or Draft/Superseded/Archived when the spec cannot proceed.", ct: ct);
            if (outcome.BudgetExhausted)
                throw new InvalidOperationException("Groomer exhausted its completion budget; partial tasks remain ineligible.");
            if (completionRequested)
            {
                var finalRows = await new GroomingDecompositionStore((IssueStore)_issues, _specs)
                    .ReadPendingAsync(specId, ct, allowFinishedHistory: spec.Status == SpecStatus.Groomed);
                foreach (var row in finalRows.Where(r => r.Type != "story")) await RequireOutcomeBindingAsync(row.Id, ct);
                await _specs.SetStatusAsync(specId, SpecStatus.Groomed, ct);
            }
            var refreshed = await _specs.GetAsync(specId, ct);
            if (refreshed?.Status == SpecStatus.Grooming)
                throw new InvalidOperationException("Groomer ended without a committed completion decision; partial tasks remain ineligible.");
            _events.Publish(new DashboardEvent(DateTime.UtcNow,
                "groomer.run.completed", specId,
                $"status={refreshed?.Status} stories={createdStoryIds.Count} tasks={createdTaskIds.Count}",
                new Dictionary<string, object?>
                {
                    ["specId"] = specId,
                    ["runId"] = _runId,
                    ["storyIds"] = string.Join(",", createdStoryIds),
                }));
            return new GroomResult(createdStoryIds.Skip(reusedStories).ToArray(), createdTaskIds.Skip(reusedTasks).ToArray());
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
        foreach (var id in createdStoryIds)
        {
            var existing = await _issues.GetAsync(id, ct);
            if (existing?.Title != title) continue;
            if (existing.Status != IssueStatus.Pending || existing.Type != "story" || existing.ParentIssueId != specIdArg)
                return "existing_story_is_not_pending_in_this_spec";
            return (existing.Description ?? "") == (description ?? "")
                ? existing.Id : "existing_story_differs_reuse_its_exact_id_and_scope";
        }
        // Structural cap — the prompt's 1-3 story limit is enforced
        // here because weaker models ignore prompt constraints.
        if (createdStoryIds.Count >= 3) return "story_limit_reached";
        var story = await _issues.CreateAsync(new NewIssue(
            Type: "story", Title: title, Description: description ?? "",
            ParentId: specIdArg, Priority: 2), ct);
        createdStoryIds.Add(story.Id);
        return story.Id;
    }

    private async Task<string> CreateTaskAsync(
        string specIdArg, string storyId, string title, string? description, int? priority, string? taskType,
        List<string> createdStoryIds, List<string> createdTaskIds,
        Dictionary<string, int> taskCountByStory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return "title_required";
        if (string.IsNullOrWhiteSpace(storyId)) return "story_id_required";
        // The parent must be a story THIS run created. Models pass bare
        // numbers ("39" for "story-39") or hallucinated ids; without
        // validation the task is written with an unresolvable parent —
        // the spec tree shows the story as taskless, the story never
        // auto-closes, and the spec strands forever (observed live
        // 2026-08-09, surfaced 2026-08-17: porthorizon
        // spec-257a4c26's 9 tasks landed with parent_issue_id="39"
        // instead of "story-39" and the story sat Pending-but-empty
        // until the operator noticed sprints had stopped).
        var normalizedStoryId = ResolveCreatedStoryId(storyId, createdStoryIds);
        if (normalizedStoryId is null)
        {
            return $"unknown_story_id: '{storyId}' was not created by this run. " +
                $"Valid story ids: {(createdStoryIds.Count == 0 ? "(none yet — call create_story first)" : string.Join(", ", createdStoryIds))}. " +
                "Pass the exact id returned by create_story.";
        }
        var parent = await _issues.GetAsync(normalizedStoryId, ct);
        if (parent is not { Status: IssueStatus.Pending, Type: "story" } || parent.ParentIssueId != specIdArg)
            return "story_is_not_pending_in_this_spec";
        foreach (var id in createdTaskIds)
        {
            var existing = await _issues.GetAsync(id, ct);
            if (existing?.ParentIssueId != normalizedStoryId || existing.Title != title) continue;
            if (existing.Status != IssueStatus.Pending) return "existing_task_is_not_pending";
            var expectedType = string.IsNullOrWhiteSpace(taskType) ? "task" : taskType.Trim().ToLowerInvariant();
            return (existing.Description ?? "") == (description ?? "") && existing.Type == expectedType && existing.Priority == (priority ?? 2)
                ? existing.Id : "existing_task_differs_reuse_its_exact_id_and_scope";
        }
        // Structural cap: 3 tasks per story (same rationale as the
        // story cap).
        var forStory = taskCountByStory.GetValueOrDefault(normalizedStoryId, 0);
        if (forStory >= 3) return "task_limit_reached_for_story";
        var task = await _issues.CreateAsync(new NewIssue(
            Type: string.IsNullOrWhiteSpace(taskType) ? "task" : taskType.Trim().ToLowerInvariant(),
            Title: title, Description: description ?? "",
            ParentId: normalizedStoryId, Priority: priority ?? 2), ct);
        createdTaskIds.Add(task.Id);
        taskCountByStory[normalizedStoryId] = forStory + 1;
        return task.Id;
    }

    /// <summary>Resolve a model-supplied story reference to a story
    /// this run created: exact id, or a bare numeric suffix ("39" →
    /// "story-39"). Null when nothing matches.</summary>
    private static string? ResolveCreatedStoryId(string storyId, List<string> createdStoryIds)
    {
        var exact = createdStoryIds.FirstOrDefault(id =>
            string.Equals(id, storyId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var trimmed = storyId.Trim().TrimStart('#');
        if (trimmed.All(char.IsDigit) && trimmed.Length > 0)
        {
            var suffix = "-" + trimmed;
            var matches = createdStoryIds
                .Where(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1) return matches[0];
        }
        return null;
    }

    private async Task<string> SetStatusAsync(
        string specIdArg, string status, CancellationToken ct)
    {
        if (!Enum.TryParse<SpecStatus>(status, ignoreCase: true, out var newStatus))
            return $"status_must_be_one_of_draft_approved_groomed_superseded_archived";
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

    /// <summary>Wire a blocks edge between two tasks of the spec being
    /// groomed. LLM-consumable errors; the store upsert is idempotent
    /// so retries are safe.</summary>
    private async Task<string> AddTaskDependencyAsync(
        string blockerId, string blockedId, string? rationale, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blockerId) || string.IsNullOrWhiteSpace(blockedId))
            return "error: blocker_id and blocked_id are required";
        try
        {
            await _issues.AddDependencyAsync(blockerId, blockedId, IssueDepKind.Blocks, ct);
            return "ok";
        }
        catch (InvalidOperationException ex)
        {
            return $"error: {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>
    /// Technical grooming for a single ad-hoc task (operator-enqueued
    /// or agent-filed follow-up). The sprint assembler refuses
    /// ad-hoc tasks until this pass marks them <c>groomed=true</c>:
    /// the groomer verifies the task against the project vision and
    /// plans it against the current state (open work + repo shape),
    /// then either approves it for sprint ingest or closes it as
    /// obsolete/duplicate/out-of-vision.
    /// Returns "groomed" | "closed" | "skipped" | null (LLM failure).
    /// </summary>
    public async Task<string?> GroomTaskAsync(string issueId, CancellationToken ct = default)
    {
        var issue = await _issues.GetAsync(issueId, ct);
        if (issue is null)
        {
            _logger.LogWarning("GroomerAgent.GroomTaskAsync: {Id} not found", issueId);
            return null;
        }
        if (issue.Status != IssueStatus.Pending
            || AgentTaskTypes.IsContainer(issue.Type)
            || issue.Type == AgentTaskTypes.PrWatch
            || issue.ParentIssueId is not null
            || string.Equals(issue.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return "skipped";
        }

        var grounding = await BuildGroundingBlockAsync(projectId: _projectId, ct, excludeIssueId: issue.Id);
        if (_issues is IssueStore owningStore && await new ActivePrerequisiteGrooming(owningStore).ReadPlanAsync(issue.Id, ct) is { } activePlan)
            grounding += "\n## Exact committed acceptance for this active-sprint prerequisite\n"
                + SprintPlanJson.Serialize(new { activePlan.Hash, activePlan.Objective, activePlan.Criteria, activePlan.Members })
                + "\nBind only existing criterion IDs with their exact text, evidence kind and scope. Do not add a duplicate handoff criterion or expand the active outcome. Map necessary implementation to existing MergedAndVerified or ImplementationVerified acceptance without changing its kind. Keep operator observations pending. Required task IDs may reference this task and committed members only. If the task cannot satisfy this existing scope, leave it ungroomed and explain the conflict.\n";
        var runProjectId = _projectId;

        var approveTool = AIFunctionFactory.Create(
            ([Description("One sentence: why this task serves the vision and how it fits current state.")] string note,
             [Description("Priority 1-5 (1 = most important) RELATIVE to the open work below: where does this task rank among what is already planned? Do not default to the middle — most follow-ups are polish and belong at 3-5; only sprint-blocking or operator-urgent work earns 1.")] int priority) =>
                ApproveTaskAsync(issue.Id, note, priority, ct),
            name: "approve_task",
            description: "Mark the task groomed (eligible for sprint ingest) and set its priority relative to the rest of the open work.");
        var closeTool = AIFunctionFactory.Create(
            ([Description("Why the task is being closed: obsolete, duplicate of existing work, or outside the vision.")] string reason) =>
                CloseTaskAsync(issue.Id, reason, ct),
            name: "close_task",
            description: "Close the task as obsolete, duplicate, or out-of-vision.");

        var systemPrompt = $"""
            You are the GroomerAgent performing TECHNICAL GROOMING of one
            ad-hoc task. The task is not sprint-eligible until you approve it.

            Steps:
            1. VERIFY AGAINST THE VISION: does the task serve the project
               vision below? If it contradicts the vision or lies outside
               it, call close_task.
            2. PLAN AGAINST CURRENT STATE: is the work already planned or
               done (see the open-work digest + repo shape below)? If it
               duplicates existing work or is obsolete, call close_task.
            3. PRIORITIZE IN SCOPE: rank the task against ALL the open
               work below (each line shows its current priority). Sprint
               assembly builds the highest-priority theme first, so the
               priority you set decides when this work ships. Use the
               full 1-5 range — an undifferentiated pile of 3s makes
               assembly order arbitrary.
            4. Otherwise call approve_task with a one-sentence note
               recording why it belongs and any sizing/approach guidance
               for the engineering agent, plus the priority from step 3.

            Bind explicit approved outcome scope before approve_task. If this is new scope without approval, leave it ungroomed and explain the intake requirement; do not fabricate approval.

            {grounding}
            """;

        var chatClient = _chatClientFactory.Create(_config, AgentType.Groomer, runProjectId);
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();
        var agent = new ChatClientAgent(
            chatClient,
            instructions: systemPrompt + OutcomeInstructions,
            name: "task-groomer",
            description: $"Task groomer for {issue.Id}",
            tools: new List<AITool> { approveTool, closeTool, CreateOutcomeBindingTool(id => id == issue.Id, ct), CreateApprovedScopeTool(ct), CreateOutcomeCatalogTool(ct), CreateOutcomeReadTool(ct) });

        var followUpOf = issue.GetMetadata("followUpOf");
        var userMessage = new ChatMessage(ChatRole.User, $"""
            Groom this ad-hoc task. Call approve_task or close_task.

            Task {issue.Id} (priority {issue.Priority}): {issue.Title}
            Description:
            ```
            {issue.Description ?? "(none)"}
            ```
            Operator re-groom reason: {issue.GetMetadata("regroomReason") ?? "(none)"}
            Filed by: {issue.GetMetadata("source") ?? "operator"}{(followUpOf is not null ? $" — follow-up of {followUpOf}" : "")}
            """);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "groomer.task.started",
            issue.Id, $"runId={_runId}", new Dictionary<string, object?>
            { ["issueId"] = issue.Id, ["runId"] = _runId }));

        string? outcome = null;
        try
        {
            await new PipelineAgentRunner(_logger).RunAsync(
                agent, new[] { userMessage }, roleLabel: "task-groomer", ct: ct);
            var after = await _issues.GetAsync(issue.Id, ct);
            outcome = after is null ? null
                : after.Status == IssueStatus.Closed ? "closed"
                : string.Equals(after.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase) ? "groomed"
                : null;
            _events.Publish(new DashboardEvent(DateTime.UtcNow, "groomer.task.completed",
                issue.Id, $"outcome={outcome ?? "no-decision"} runId={_runId}",
                new Dictionary<string, object?>
                { ["issueId"] = issue.Id, ["runId"] = _runId, ["outcome"] = outcome }));
            return outcome;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GroomerAgent.GroomTaskAsync failed for {Id}", issue.Id);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, "groomer.task.failed",
                issue.Id, ex.Message, new Dictionary<string, object?>
                { ["issueId"] = issue.Id, ["runId"] = _runId, ["error"] = ex.Message }));
            throw;
        }
    }

    private const string OutcomeInstructions = """

        SPRINT OUTCOME CONTRACT: use bind_outcome for every task before completing grooming.
        Use list_outcomes for a bounded existing-plan catalog; read_outcome(taskId) inspects explicit follow-up provenance.
        Read the actual approved scope with read_approved_scope; use the exact proposalId/revision/hash/specId
        and a literal approvedExcerpt from its approved body. Scope cannot be inferred from a title or project vision.
        Describe a small independently useful delivery unit and explicit testable acceptance criteria.
        Ten tasks INCLUDING prerequisites is a ceiling, not a target. Prefer natural independent pieces
        that use configured role capacities, but one or two coherent tasks is valid; never pad or split artificially.
        Tasks in one complete delivery unit share outcomeId, objective, criteria and complete requiredTaskIds.
        Use a common outcomeId/objective across related approved specifications only with explicit semantic justification.
        criterionIds specifies each task's contribution. This maintenance bridge emits MergedAndVerified for new implementation criteria. ImplementationVerified is available only after the explicit format42 upgrade; do not create new mappings of that kind on41. Preserve every existing criterion kind and never weaken a requirement for a new merged PR. Other kinds are
        DeploymentHealthy (post-deploy controller/runtime qualification), or OperatorObserved (post-deploy operator-led acceptance,
        including visual/browser checks). Never substitute deployment health or merged code for actual visual acceptance.
        Keep pre-deployment implementation and post-deployment acceptance as distinct criteria WITHIN THE SAME delivery outcome.
        Each engineering task's own criterionIds MUST include a genuine ImplementationVerified or MergedAndVerified criterion; it may also contribute
        to DeploymentHealthy or OperatorObserved criteria. Attach those later criteria to the implementation delivery members.
        requiredTaskIds contains actual pre-deployment engineering work, never separate operator-observation-only QA tasks
        or a second observation-only sprint. Do not invent code changes, PRs, or fake operator evidence to pass this rule.
        Deployment proceeds after genuine implementation verification; post-deployment acceptance remains honestly pending
        until actual runtime qualification or operator observation. Only the operator can record operator-observed evidence.
        reason is Delivers, Remediates, or Prerequisite. Every prerequisite needs explicit criterion mapping too.
        For remediation, bind to existing approved acceptance scope; followUpOf alone is not approval.
        Binding JSON: {"version":1,"outcomeId":"stable-goal-id","objective":"useful outcome",
          "criteria":[{"id":"criterion-id","text":"observable acceptance","evidenceKind":"MergedAndVerified",
            "scope":{"specId":"...","proposalId":"...","revision":1,"hash":"...","approvedExcerpt":"literal approved text"}}],
          "requiredTaskIds":["all tasks required for this unit"],"criterionIds":["criterion-id"],"reason":"Delivers",
          "authority":{"specId":"...","proposalId":"...","revision":1,"hash":"...","approvedExcerpt":"literal approved text"}}
        After wiring real prerequisite edges and binding all tasks, request Groomed/approve_task.
        """;

    private AIFunction CreateOutcomeBindingTool(Func<string, bool> allowed, CancellationToken ct) => AIFunctionFactory.Create(
        async (string taskId, string bindingJson) =>
        {
            if (!allowed(taskId)) return "error: task is outside this grooming run";
            if (_issues is not IssueStore store) return "error: owning project store required";
            try
            {
                var binding = System.Text.Json.JsonSerializer.Deserialize<SprintOutcomeBinding>(bindingJson, SprintPlanJson.Options)
                    ?? throw new InvalidOperationException("Outcome binding is required.");
                await new SprintOutcomeAuthority(store).BindAsync(taskId, binding, ct);
                return "bound";
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            { return "error: " + ex.Message; }
        }, name: "bind_outcome", description: "Bind one task to an exact approved scope and complete independently useful delivery unit.");

    private AIFunction CreateOutcomeCatalogTool(CancellationToken ct) => AIFunctionFactory.Create(
        async () => _issues is IssueStore store
            ? SprintPlanJson.Serialize((await new SprintPlanningStore(store).CandidatesAsync(20, ct: ct)).Select(c => new { c.Id, c.Title, c.Priority, binding = c.BindingJson is { Length: <= 12000 } ? c.BindingJson : "Use read_outcome for the complete mapping." }))
            : "error: owning project store required",
        name: "list_outcomes", description: "Read at most 20 pending task outcome mappings; this is a bounded catalog, not the full backlog.");

    private AIFunction CreateOutcomeReadTool(CancellationToken ct) => AIFunctionFactory.Create(
        async (string taskId) =>
        {
            var task = await _issues.GetAsync(taskId, ct);
            return task is null ? "error: task not found" : SprintPlanJson.Serialize(SprintPlanJson.ReadBinding(task));
        }, name: "read_outcome", description: "Read an existing task's exact outcome mapping, including a completed follow-up source. This does not itself approve new scope.");

    private AIFunction CreateApprovedScopeTool(CancellationToken ct) => AIFunctionFactory.Create(
        async (string specId, int offset = 0) =>
        {
            if (_issues is not IssueStore store) return "error: owning project store required";
            var receipt = await new Forge.Core.Initiatives.InitiativeIngestionStore(store).GetBySpecAsync(specId, ct);
            if (receipt is null || receipt.RefinementState != "Completed") return "error: no completed approved ingestion for this specification";
            var epic = await _issues.GetAsync(receipt.EpicId, ct);
            var body = epic?.Description ?? "";
            offset = Math.Clamp(offset, 0, body.Length);
            return SprintPlanJson.Serialize(new { specId, receipt.ProposalId, receipt.Revision, receipt.Hash,
                offset, totalLength = body.Length, approvedBody = body.Substring(offset, Math.Min(8000, body.Length - offset)) });
        }, name: "read_approved_scope", description: "Read exact approved scope identity and bounded approved body for a project specification.");

    private async Task RequireOutcomeBindingAsync(string taskId, CancellationToken ct)
    {
        var task = await _issues.GetAsync(taskId, ct) ?? throw new InvalidOperationException("Task missing.");
        var binding = SprintPlanJson.ReadBinding(task) ?? throw new InvalidOperationException("Task requires approved outcome mapping before grooming can complete.");
        SprintOutcomeAuthority.ValidateShape(taskId, binding);
        if (_issues is not IssueStore store) throw new InvalidOperationException("Owning project store required.");
        foreach (var scope in binding.Criteria.Select(c => c.Scope).Append(binding.Authority).Distinct())
            await new SprintOutcomeAuthority(store).ValidateAsync(scope, ct);
    }

    private async Task<string> ApproveTaskAsync(string issueId, string note, int priority, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(note)) return "note_required";
        var cur = await _issues.GetAsync(issueId, ct);
        if (cur is null) return "issue_not_found";
        await RequireOutcomeBindingAsync(issueId, ct);
        var clamped = Math.Clamp(priority, 1, 5);
        if (cur.Priority != clamped)
        {
            await _issues.SetPriorityAsync(issueId, clamped, ct);
        }
        var meta = new Dictionary<string, object>();
        meta["groomed"] = "true";
        meta["groomedAt"] = DateTime.UtcNow.ToString("O");
        meta["groomNote"] = note;
        meta["groomRunId"] = _runId;
        await ((IssueStore)_issues).MergePendingMetadataAsync(issueId, meta, ct);
        // groomed=true is a metadata-only write, so the store's
        // lifecycle choke point publishes nothing — and the assembler /
        // dispatch fast path would wait for the 15m backstop. This is
        // an operator "unstick it now" lever: kick explicitly (hint
        // only; consumers re-read the store).
        if (_issues is IssueStore anchor)
        {
            var kickedAt = DateTimeOffset.UtcNow;
            await anchor.Events.PublishAsync(new Core.Messaging.TaskEnqueued
            {
                MessageId = Core.Messaging.TaskEnqueued.IdFor(anchor.ProjectId, issueId, kickedAt),
                ProjectId = anchor.ProjectId,
                TaskId = issueId,
                TaskType = cur.Type,
                EnqueuedAt = kickedAt,
            }, ct);
        }
        _logger.LogInformation("Task {Id} groomed (approved, P{Priority}): {Note}", issueId, clamped, note);
        return "approved";
    }

    private async Task<string> CloseTaskAsync(string issueId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "reason_required";
        var cur = await _issues.GetAsync(issueId, ct);
        if (cur is null) return "issue_not_found";
        var meta = ReadMetadata(cur);
        meta["groomedAt"] = DateTime.UtcNow.ToString("O");
        meta["groomCloseReason"] = reason;
        meta["groomRunId"] = _runId;
        await _issues.TransitionAsync(issueId, IssueStatus.Closed, error: null, metadata: meta, ct: ct);
        // A plain status transition publishes nothing (the store's
        // choke point fires on the lifecycle state pair only), so an
        // all-CLOSED follow-up batch would leave the assembler parked
        // in awaiting-groom until the backstop. Same explicit-kick
        // pattern as ApproveTaskAsync — hint only.
        if (_issues is IssueStore closeAnchor)
        {
            var kickedAt = DateTimeOffset.UtcNow;
            await closeAnchor.Events.PublishAsync(new Core.Messaging.TaskEnqueued
            {
                MessageId = Core.Messaging.TaskEnqueued.IdFor(closeAnchor.ProjectId, issueId, kickedAt),
                ProjectId = closeAnchor.ProjectId,
                TaskId = issueId,
                TaskType = cur.Type,
                EnqueuedAt = kickedAt,
            }, ct);
        }
        _logger.LogInformation("Task {Id} closed by grooming: {Reason}", issueId, reason);
        return "closed";
    }

    /// <summary>
    /// The grooming ground truth: project vision (from the
    /// <c>vision/master</c> memory key) + a digest of open work (so
    /// the groomer doesn't re-plan what exists) + the repo shape (so
    /// plans reflect the real codebase). Missing pieces degrade to
    /// explicit "none" markers rather than silence.
    /// </summary>
    private async Task<string> BuildGroundingBlockAsync(string? projectId, CancellationToken ct, string? excludeIssueId = null)
    {
        var sb = new StringBuilder();

        var configured = _roleCapacitiesLookup is not null && !string.IsNullOrWhiteSpace(projectId)
            ? await _roleCapacitiesLookup(projectId, ct)
            : new Dictionary<string, int>();
        var roles = new Dictionary<string, int>(configured, StringComparer.OrdinalIgnoreCase);
        var workers = new[] { "coredev", "clientdev" };
        sb.AppendLine("## Implementation worker capacity (owning project)");
        foreach (var role in workers)
            sb.AppendLine($"- {role}: {Math.Max(0, Forge.Configuration.DefaultProjectRoles.MaxFor(roles, role))} concurrent workers");
        sb.AppendLine("Soft parallel goal: where the approved outcome naturally supports it, plan genuine independent tasks that can use these workers concurrently.");
        sb.AppendLine("Derive this soft goal from the configured limits above: keep each implementation role productively occupied where coherent, independent approved work permits it. There is no fixed task-count minimum.");
        sb.AppendLine("Use fewer tasks when that is the coherent solution; never pad work, invent QA work, or remove real prerequisites to fill slots. QA and Reviewer capacity are not implementation capacity.");
        sb.AppendLine("QA verifies an existing PR in the optional watch lane; it is not an extra implementation task to fill a parallel slot. Assign genuine automated test-code changes to CoreDev or ClientDev within their editing territory, not a standalone QA/reviewer task that cannot produce its own merged implementation.");
        sb.AppendLine("Ten engineering tasks including prerequisites is a ceiling, never a fill target. Story/task tool caps still apply; they are maxima, not targets.");
        sb.AppendLine();
        var territories = _roleTerritoriesLookup is not null && !string.IsNullOrWhiteSpace(projectId)
            ? await _roleTerritoriesLookup(projectId, ct) : null;
        sb.AppendLine("## Actual implementation role territories (owning project)");
        var registry = new RoleAgentRegistry();
        foreach (var role in workers)
        {
            var resolved = RoleAgentRegistry.ResolveTerritory(registry.ByAgentName(role)!, territories);
            sb.AppendLine(RoleAgentRegistry.DescribeTerritory(role, resolved.Prefixes, resolved.AllowsRootFiles));
        }
        sb.AppendLine("Assign every required file deliverable to a role authorized for its destination. Do not assign ClientDev a docs/evidence edit outside its actual territory, invent a substitute destination, or silently omit an explicit deliverable. If no authorized role can fulfill a required destination, surface the assignment conflict rather than marking decomposition complete.");
        sb.AppendLine("Worker commands/results and a pending operator handoff can be recorded in the run transcript; that is not a substitute for an explicitly required repository document. Assign any required repository collation to an authorized implementation owner. Actual visual/running-image qualification and final operator acceptance remain operator-owned and pending until observed; workers must not fabricate them.");
        sb.AppendLine();
        sb.AppendLine("## Project vision");
        // Per-project vision wins (vision/<projectId>); the legacy
        // global vision/master is the fallback so single-project
        // deployments need no re-keying.
        string? vision = null;
        if (_memory is not null)
        {
            if (!string.IsNullOrWhiteSpace(projectId))
                vision = (await _memory.RecallAsync($"vision/{projectId}", ct)).FirstOrDefault()?.Body;
            if (string.IsNullOrWhiteSpace(vision))
                vision = (await _memory.RecallAsync("vision/master", ct)).FirstOrDefault()?.Body;
        }
        sb.AppendLine(string.IsNullOrWhiteSpace(vision)
            ? "(no vision document — the operator has not written one; judge against the spec/task text alone)"
            : vision.Length > 4000 ? vision[..4000] + "\n...[truncated]..." : vision);

        sb.AppendLine();
        sb.AppendLine("## Open work (already planned or in flight — do not re-plan)");
        var open = new List<IssueRecord>();
        open.AddRange(await _issues.ListAsync(new IssueFilter { Status = IssueStatus.Pending }, ct));
        open.AddRange(await _issues.ListAsync(new IssueFilter { Status = IssueStatus.InProgress }, ct));
        // Never list the candidate itself: seeing itself in the digest
        // makes the model close its own task as a "duplicate of
        // existing work" (observed live, task-152).
        open.RemoveAll(i => i.Id == excludeIssueId);
        if (open.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var i in open.Take(60))
            {
                sb.AppendLine($"- {i.Id} [P{i.Priority} {i.Type}/{i.Status}] {i.Title}");
            }
            if (open.Count > 60) sb.AppendLine($"- ...and {open.Count - 60} more");
        }

        sb.AppendLine();
        sb.AppendLine("## Repo shape (top levels of the current codebase)");
        sb.AppendLine(BuildRepoShape(projectId));

        return sb.ToString();
    }

    private string BuildRepoShape(string? projectId = null)
    {
        var root = _projectRoot;
        if (!string.IsNullOrWhiteSpace(projectId) && _projectRootLookup is not null)
        {
            try
            {
                root = _projectRootLookup(projectId) ?? root;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "project root lookup failed for {ProjectId}; using default root", projectId);
            }
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return "(unavailable)";
        }
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", "bin", "obj", "node_modules", ".forge", ".portHorizon", ".vs" };
        var lines = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                if (skip.Contains(name)) continue;
                lines.Add($"{name}/");
                if (lines.Count >= 80) break;
                foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d).Take(8))
                {
                    var subName = Path.GetFileName(sub);
                    if (skip.Contains(subName)) continue;
                    lines.Add($"  {subName}/");
                }
            }
            foreach (var file in Directory.GetFiles(root).OrderBy(f => f).Take(20))
            {
                lines.Add(Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.Message})";
        }
        return lines.Count == 0 ? "(empty)" : string.Join("\n", lines.Take(100));
    }

    private static Dictionary<string, object> ReadMetadata(IssueRecord issue)
    {
        var meta = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(issue.MetadataJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(issue.MetadataJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                        meta[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? p.Value.GetString()!
                            : p.Value.GetRawText();
                    }
                }
            }
            catch { /* malformed metadata: start fresh */ }
        }
        return meta;
    }
}

/// <summary>Stories + tasks created by a groom run (ids).</summary>
public sealed record GroomResult(IReadOnlyList<string> StoryIds, IReadOnlyList<string> TaskIds);
