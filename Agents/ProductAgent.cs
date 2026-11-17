using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;
using Forge.Specs;

namespace Forge.Agents;

/// <summary>
/// ProductAgent: refines an intake-draft spec into a fully
/// structured form. Triggered by the operator accepting a child
/// epic on the Intake tab.
///
/// <para>
/// The agent reads the current spec body (intake draft) and
/// rewrites it with the standard sections (## Summary, ##
/// Acceptance criteria, ## Diagrams, ## Touches, ## Out of
/// scope, ## Open questions, ## Dependencies). When the agent
/// is confident the refinement captures the operator's intent
/// and the codebase context, it commits the new body via
/// <c>update_spec</c>. The spec moves to <c>Draft</c> after
/// refinement (per the resolved Q2.5 — operator-approved at the
/// master level, product refines the body in place).
/// </para>
///
/// <para>
/// Version history: the new body becomes a new spec_version
/// with author "product:&lt;run_id&gt;" so the operator can
/// distinguish their own edits from product edits in the
/// history view.
/// </para>
///
/// <para>
/// The agent runs in the background (fire-and-forget from
/// AcceptProposedEpicAsync). It does not block the operator's
/// accept-click UX; the dashboard sees the refined spec on the
/// next refresh via the SSE stream.
/// </para>
/// </summary>
public sealed class ProductAgent
{
    private readonly ISpecStore _specs;
    private readonly IIssueStore _issues;
    private readonly IProjectContextSource _projectContext;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly ISkillSource? _skills;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<ProductAgent> _logger;
    private readonly string _rolePromptsRoot;
    private readonly string _runId;

    public ProductAgent(
        ISpecStore specs,
        IIssueStore issues,
        IProjectContextSource projectContext,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ILogger<ProductAgent> logger,
        ISkillSource? skills = null,
        string rolePromptsRoot = "agents",
        string? runId = null)
    {
        _specs = specs;
        _issues = issues;
        _projectContext = projectContext;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _logger = logger;
        _skills = skills;
        _rolePromptsRoot = rolePromptsRoot;
        // Each run gets a stable id so the spec_version author can be
        // traced to a specific agent run. Defaults to a fresh guid
        // but tests pass a deterministic id.
        _runId = runId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    public string RunId => _runId;

    /// <summary>
    /// Refine a spec. Returns the updated spec on success, or
    /// null if the agent didn't call update_spec (e.g. the LLM
    /// decided no refinement was needed).
    /// </summary>
    public async Task<SpecRecord?> RefineSpecAsync(
        string specId, string projectId, CancellationToken ct = default)
    {
        var current = await _specs.GetAsync(specId, ct);
        if (current is null)
        {
            _logger.LogWarning("ProductAgent.RefineSpecAsync: spec {Id} not found", specId);
            return null;
        }
        if (!string.Equals(current.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Product refinement spec belongs to another project.");

        var context = await _projectContext.BuildAsync(projectId, ct);
        var skillContents = _skills is null
            ? Array.Empty<SkillContent>()
            : await _skills.LoadForRoleAsync(AgentType.CoreDev, projectId, ct);
        var skills = string.Join("\n\n", skillContents.Select(s => s.Body));

        var roleInstructions = LoadRoleInstructions("product");
        var systemPrompt = $"""
            You are the ProductAgent for project {projectId}. Your job: take
            a spec created from an explicitly approved Intake proposal
            and refine that approved scope into a structured planning document.
            The proposal's approval authorizes its scope; the spec's Draft status
            describes its current pipeline stage, not an unapproved initiative.
            Do not broaden or re-approve the proposal, change its title/parent,
            or claim that refinement itself completes design or technical grooming.

            Use the `update_spec(spec_id, body, author)` tool to commit
            the refined body. author MUST be "product:{_runId}" so the
            version history distinguishes your edits from operator edits.

            The refined body must use this template:

            ## Summary
            One paragraph restating the spec in operator-friendly terms.

            ## Acceptance criteria
            - [ ] concrete testable behavior
            - [ ] ...

            ## Diagrams
            ```mermaid
            sequenceDiagram
              participant A
              participant B
              A->>B: step
            ```

            (Include at least one `flowchart`, `sequenceDiagram`, or
            `graph` block per non-trivial spec. Domain-level
            service-to-service interactions are preferred over
            file-level detail.)

            ## Touches
            - Module.Path.Name
                - one-line rationale if relevant

            ## Out of scope
            - explicit non-goal

            ## Open questions
            - question that still needs an answer

            ## Dependencies
            - blocks spec-target-id — one-line reason
            - depends_on spec-other-id
            - related spec-foo

            Dependencies are exclusively existing spec IDs owned by this project.
            Never put epic, story, task, proposal, or the current spec's own ID here.
            Parent issue links such as epic-1 belong in Notes, not the spec dependency graph.
            If an exact spec ID is unknown, omit the dependency rather than invent it.
            The tool rejects missing, foreign-project, self-referential or circular
            references before saving. Read its error, repair the body, and retry
            update_spec in this run; an error is not a committed refinement.

            ## Notes
            (optional) anything that didn't fit above.

            Don't add sections that are empty. Don't repeat the
            intake draft verbatim — refine, structure, and add the
            diagrams + touch list + dep list.

            {roleInstructions}

            ## Project context
            (Open issues, recent specs, and project skills below.
            Use them to avoid proposing work that conflicts with
            in-flight effort.)

            {FormatContextForPrompt(context)}

            ## Project skills
            {skills}
            """;

        var chatClient = _chatClientFactory.Create(_config, AgentType.CoreDev, projectId);
        // UseFunctionInvocation: required so the agent's tool calls
        // actually execute the function and feed the result back.
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();

        SpecRecord? updatedThisRun = null;
        var updateSpecTool = AIFunctionFactory.Create(
            async ([Description("Spec id (must match the current spec).")] string specIdArg,
             [Description("Full refined spec body in markdown.")] string body,
             [Description("Optional display hint; the server records the canonical product run author.")] string? author = null) =>
            {
                if (specIdArg != specId) return "error: update is restricted to the current spec";
                if (string.IsNullOrWhiteSpace(body)) return "error: body required";
                if (body.Length > 256_000) return "error: body too large (max 256KB)";
                var dependencyError = await ValidateDependenciesAsync(specId, projectId, body, ct);
                if (dependencyError is not null) return "error: " + dependencyError;
                var updated = await UpdateSpecAsync(specId, projectId, body, ct);
                if (updated is not null) updatedThisRun = updated;
                return updated is null ? "spec_not_found" : $"version={updated.CurrentVersion}";
            },
            name: "update_spec",
            description: "Replace the spec body with a fully refined version. " +
                         "Pass the full markdown body, not a diff. " +
                         "Author should be 'product:<runId>' so the version history distinguishes product edits from operator edits.");

        var tools = new List<AITool> { updateSpecTool };
        var agent = new ChatClientAgent(
            chatClient,
            instructions: systemPrompt,
            name: "product",
            description: $"Product agent refining {specId}",
            tools: tools);

        var userMessage = new ChatMessage(ChatRole.User, $"""
            Refine this spec. Keep the title ({current.Title}) and
            parent_issue_id unchanged. Replace the body with the
            structured template.

            Current spec body (intake draft):
            ```
            {current.Body}
            ```
            """);

        _events.Publish(new DashboardEvent(DateTime.UtcNow, "product.run.started",
            specId, $"project={projectId} runId={_runId}",
            new Dictionary<string, object?> { ["specId"] = specId, ["runId"] = _runId }));

        try
        {
            await new PipelineAgentRunner(_logger).RunAsync(
                agent, new[] { userMessage }, roleLabel: "product", ct: ct);
            var refreshed = updatedThisRun;
            _events.Publish(new DashboardEvent(DateTime.UtcNow,
                refreshed is null ? "product.run.failed" : "product.run.completed",
                specId, refreshed is null ? "no committed refinement from this run" : $"version={refreshed.CurrentVersion}",
                new Dictionary<string, object?>
                {
                    ["specId"] = specId,
                    ["runId"] = _runId,
                    ["version"] = refreshed?.CurrentVersion,
                }));
            return refreshed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductAgent refine failed for {Spec}", specId);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, "product.run.failed",
                specId, ex.Message, new Dictionary<string, object?>
                {
                    ["specId"] = specId,
                    ["runId"] = _runId,
                    ["error"] = ex.Message,
                }));
            throw;
        }
    }

    /// <summary>
    /// The body of the update_spec AIFunction. We cap the body
    /// size defensively (a runaway LLM could otherwise spew
    /// millions of bytes into a spec_version row).
    /// </summary>
    private async Task<SpecRecord?> UpdateSpecAsync(
        string specId, string projectId, string body, CancellationToken ct)
    {
        var current = await _specs.GetAsync(specId, ct);
        if (current is null) return null;
        if (current.ProjectId != projectId)
            throw new InvalidOperationException("Product refinement spec ownership changed.");
        return await _specs.UpdateBodyAsync(specId,
            new UpdateSpecBody(body, $"product:{_runId}"), ct);
    }

    private async Task<string?> ValidateDependenciesAsync(string specId, string projectId, string body, CancellationToken ct)
    {
        var extractor = new SpecBodyExtractor();
        var pending = new Queue<string>(extractor.Extract(body).Deps.Select(d => d.TargetSpecId));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var targetId = pending.Dequeue();
            if (string.Equals(targetId, specId, StringComparison.OrdinalIgnoreCase))
                return $"Dependency '{targetId}' creates a self-reference or circular chain. Remove that dependency and retry update_spec.";
            if (!visited.Add(targetId)) continue;
            if (visited.Count > 100)
                return "Dependency graph exceeds the 100-spec validation limit. Reduce the declared dependencies or request operator review; no body was saved.";
            var target = await _specs.GetAsync(targetId, ct);
            if (target is null || target.ProjectId != projectId)
                return $"Dependency '{targetId}' is not an existing spec in project '{projectId}'. Use a real same-project spec ID or omit it. Put parent epic/story/task links in Notes, then retry update_spec.";
            foreach (var dependency in extractor.Extract(target.Body).Deps)
                pending.Enqueue(dependency.TargetSpecId);
        }
        return null;
    }

    private string LoadRoleInstructions(string agentName)
    {
        var path = Path.Combine(_rolePromptsRoot, agentName + ".md");
        if (!File.Exists(path)) return string.Empty;
        return File.ReadAllText(path);
    }

    private static string FormatContextForPrompt(ProjectContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        if (ctx.OpenIssues.Count > 0)
        {
            sb.AppendLine("### Open issues (Pending or InProgress)");
            foreach (var i in ctx.OpenIssues.Take(15))
                sb.AppendLine($"- `{i.Id}` [{i.Type}] {i.Title} (status={i.Status}, priority={i.Priority})");
        }
        if (ctx.RecentSpecs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Recent specs (last 5)");
            foreach (var s in ctx.RecentSpecs.Take(5))
                sb.AppendLine($"- `{s.Id}` {s.Title} (status={s.Status}, v{s.CurrentVersion})");
        }
        return sb.ToString();
    }
}
