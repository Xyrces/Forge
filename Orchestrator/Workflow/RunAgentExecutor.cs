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
        var prompt = BuildPrompt(issue, role, worktreePath, branch, input.BaseBranch);
        var queued = drainMessageBus(roleAgent.KiloAgentName);
        var fullPrompt = string.IsNullOrEmpty(queued)
            ? prompt
            : prompt + "\n\n## Operator messages\n" + queued + "\n\nAddress these messages before working on the task.";

        var result = await runner.RunAsync(
            role, fullPrompt,
            sessionId: null,
            context: new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = branch,
                ["issueId"] = issue.Id,
            },
            ct);
        return new AgentCompleted(input, AgentResult.Ok, result.Text, result.SessionId);
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