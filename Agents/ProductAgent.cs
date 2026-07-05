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
    private readonly string _kiloAgentsRoot;
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
        string kiloAgentsRoot = ".kilo/agents",
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
        _kiloAgentsRoot = kiloAgentsRoot;
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

        var context = await _projectContext.BuildAsync(projectId, ct);
        var skillContents = _skills is null
            ? Array.Empty<SkillContent>()
            : await _skills.LoadForRoleAsync(AgentType.CoreDev, ct);
        var skills = string.Join("\n\n", skillContents.Select(s => s.Body));

        var roleInstructions = LoadRoleInstructions("product");
        var systemPrompt = $"""
            You are the ProductAgent for project {projectId}. Your job: take
            a spec that the intake agent drafted during a conversation
            and refine it into the structured form the operator will
            review before grooming starts.

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

            (Only declare deps for specs you can name by id. If
            you're unsure of the id, omit the dep; the operator
            can add it later from the dashboard.)

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

        var chatClient = _chatClientFactory.Create(_config, AgentType.CoreDev);
        // UseFunctionInvocation: required so the agent's tool calls
        // actually execute the function and feed the result back.
        chatClient = new ChatClientBuilder(chatClient).UseFunctionInvocation().Build();

        var updateSpecTool = AIFunctionFactory.Create(
            ([Description("Spec id (must match the current spec).")] string specIdArg,
             [Description("Full refined spec body in markdown.")] string body,
             [Description("Author tag for the version. Must start with 'product:'.")] string author) =>
                UpdateSpecAsync(specIdArg, body, author, ct),
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
            var response = await agent.RunAsync(userMessage, cancellationToken: ct);
            var refreshed = await _specs.GetAsync(specId, ct);
            _events.Publish(new DashboardEvent(DateTime.UtcNow,
                refreshed is null ? "product.run.failed" : "product.run.completed",
                specId, refreshed is null ? "spec-missing" : $"version={refreshed.CurrentVersion}",
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
    private async Task<string> UpdateSpecAsync(
        string specIdArg, string body, string author, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(specIdArg))
            return "spec_id_required";
        if (string.IsNullOrWhiteSpace(body))
            return "body_required";
        if (body.Length > 256_000)
            return "body_too_large (max 256KB)";
        // Author is allowed to be whatever the agent sends; we
        // record it verbatim. The system prompt instructs the agent
        // to use the runId form, but we don't enforce it at the
        // tool layer so the agent has an escape hatch for debug.
        var updated = await _specs.UpdateBodyAsync(specIdArg,
            new UpdateSpecBody(body, string.IsNullOrWhiteSpace(author) ? $"product:{_runId}" : author), ct);
        return updated is null
            ? "spec_not_found"
            : $"version={updated.CurrentVersion}";
    }

    private string LoadRoleInstructions(string kiloAgentName)
    {
        var path = Path.Combine(_kiloAgentsRoot, kiloAgentName + ".md");
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