using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
// RoleAgentRegistry is in PortHorizon.Agents.Agents, not .Core.

namespace PortHorizon.Agents.Orchestrator.Workflow;

/// <summary>
/// Third executor in the engineering dispatch workflow. Builds
/// the prompt, drains the operator message bus, calls
/// <see cref="IAgentRunner.RunAsync"/> against the worktree, and
/// captures the model response in issue metadata. Returns an
/// <see cref="AgentCompleted"/> with the agent's text + session id.
/// </summary>
public sealed class RunAgentExecutor : FunctionExecutor<WorktreeReady, AgentCompleted>
{
    private readonly IIssueStore _issues;
    private readonly IAgentRunner _runner;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly Func<string, string?> _drainMessageBus;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<RunAgentExecutor> _logger;

    public RunAgentExecutor(
        IIssueStore issues,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        Func<string, string?> drainMessageBus,
        IDashboardEventBus events,
        ILogger<RunAgentExecutor> logger)
        : base(
            "run-agent",
            (input, ctx, ct) => HandleAsync(input, issues, runner, roleRegistry, drainMessageBus, events, logger, ct),
            null,
            new[] { typeof(WorktreeReady) },
            new[] { typeof(AgentCompleted) })
    {
        _issues = issues;
        _runner = runner;
        _roleRegistry = roleRegistry;
        _drainMessageBus = drainMessageBus;
        _events = events;
        _logger = logger;
    }

    public static async ValueTask<AgentCompleted> HandleAsync(
        WorktreeReady input,
        IIssueStore issues,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        Func<string, string?> drainMessageBus,
        IDashboardEventBus events,
        ILogger logger,
        CancellationToken ct)
    {
        if (input.Result == WorktreeResult.AlreadyClaimed)
        {
            return new AgentCompleted(input, AgentResult.Skipped, string.Empty, null);
        }
        var issue = input.Claim.Issue;
        var role = RoleAgentRegistry.FromTaskType(issue.Type);
        var branch = input.Claim.Branch ?? $"agent/{issue.Id}";
        var roleAgent = roleRegistry.ForType(role);
        var worktreePath = input.WorktreePath!;

        events.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.AgentSessionStarted,
            issue.Id, $"role={roleAgent.KiloAgentName} worktree={worktreePath}"));

        var prompt = BuildPrompt(issue, role, worktreePath, branch, input.BaseBranch);
        var queued = drainMessageBus(roleAgent.KiloAgentName);
        var fullPrompt = string.IsNullOrEmpty(queued)
            ? prompt
            : prompt + "\n\n## Operator messages\n" + queued + "\n\nAddress these messages before working on the task.";

        AgentRunResult result;
        try
        {
            result = await runner.RunAsync(
                role, fullPrompt,
                sessionId: null,
                context: new Dictionary<string, object>
                {
                    ["worktreePath"] = worktreePath,
                    ["branch"] = branch,
                    ["issueId"] = issue.Id,
                },
                ct);
        }
        catch (Exception ex)
        {
            // Record the failure in the issue's lastError metadata
            // and transition the issue to Failed (preserves the
            // old sequential code's behavior — the operator can
            // see what went wrong from the dashboard's Tasks tab).
            // The orchestrator's HandleFailureAsync is the source
            // of truth for retry policy; here we just persist the
            // error so it survives even if the orchestrator doesn't
            // catch the workflow's exception.
            var cur = await issues.GetAsync(issue.Id, ct);
            if (cur is not null)
            {
                var meta = ParseMetadata(cur.MetadataJson);
                meta["lastError"] = $"{ex.GetType().Name}: {ex.Message}";
                meta["modelResponse"] = $"<threw: {ex.GetType().Name}: {ex.Message}>";
                await issues.TransitionAsync(issue.Id, cur.Status,
                    error: meta["lastError"]?.ToString(), metadata: meta, ct: ct);
            }
            throw;
        }
        // Record the model response in issue metadata so the
        // dashboard can show what the agent said, even when
        // downstream steps fail.
        await RecordModelResponseAsync(issues, issue.Id, result.Text, ct);
        await UpdateMetadataAsync(issues, issue.Id, m =>
        {
            m["agentSessionId"] = result.SessionId ?? string.Empty;
            return m;
        }, ct);
        events.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.AgentSessionCompleted,
            issue.Id, $"elapsed={result.Elapsed.TotalMilliseconds:F0}ms",
            new Dictionary<string, object?>
            {
                ["sessionId"] = result.SessionId ?? "",
                ["elapsedMs"] = result.Elapsed.TotalMilliseconds,
            }));
        return new AgentCompleted(input, AgentResult.Ok, result.Text, result.SessionId);
    }

    private static async Task RecordModelResponseAsync(
        IIssueStore issues, string id, string response, CancellationToken ct)
    {
        var cur = await issues.GetAsync(id, ct);
        if (cur is null) return;
        var meta = ParseMetadata(cur.MetadataJson);
        meta["modelResponse"] = response ?? string.Empty;
        await issues.TransitionAsync(id, cur.Status, error: null, metadata: meta, ct: ct);
    }

    private static async Task UpdateMetadataAsync(
        IIssueStore issues, string id,
        Func<Dictionary<string, object>, Dictionary<string, object>> mutate,
        CancellationToken ct)
    {
        var cur = await issues.GetAsync(id, ct);
        if (cur is null) return;
        var current = ParseMetadata(cur.MetadataJson);
        var next = mutate(current);
        await issues.TransitionAsync(id, cur.Status, error: null, metadata: next, ct: ct);
    }

    private static Dictionary<string, object> ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new();
            var d = new Dictionary<string, object>();
            foreach (var p in doc.RootElement.EnumerateObject())
                d[p.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(p.Value.GetRawText())!;
            return d;
        }
        catch { return new(); }
    }

    /// <summary>
    /// The full prompt mirrors the original OrchestratorAgent.BuildPrompt.
    /// Kept here so the executor's logic is self-contained for tests.
    /// </summary>
    public static string BuildPrompt(
        IssueRecord issue, AgentType role, string worktreePath,
        string branch, string baseBranch)
    {
        return $"""
            You are the {role} agent.

            Issue: {issue.Id}
            Title: {issue.Title}
            Type: {issue.Type}
            Priority: {issue.Priority}

            {issue.Description ?? ""}

            Working directory: {worktreePath}
            Branch: {branch} (base: {baseBranch})
            """;
    }
}

public enum AgentResult
{
    Ok,
    Skipped,
}

/// <summary>
/// Output of <see cref="RunAgentExecutor"/>. Carries the
/// worktree-ready input + the agent's text + session id.
/// </summary>
public sealed record AgentCompleted(
    WorktreeReady Worktree,
    AgentResult Result,
    string Text,
    string? SessionId);