using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Orchestrator.Workflow;

/// <summary>
/// Second executor in the engineering dispatch workflow. Creates a
/// git worktree on a per-issue branch under the workspace, then
/// returns a <see cref="WorktreeReady"/> with the worktree path +
/// branch + base branch (for the eventual PR's base).
/// </summary>
public sealed class WorktreeExecutor : FunctionExecutor<ClaimedIssue, WorktreeReady>
{
    private readonly GitWorktreeService _worktrees;
    private readonly string _defaultBranch;
    private readonly ILogger<WorktreeExecutor> _logger;

    public WorktreeExecutor(
        GitWorktreeService worktrees,
        string defaultBranch,
        ILogger<WorktreeExecutor> logger)
        : base(
            "worktree",
            (input, ctx, ct) => HandleAsync(input, worktrees, defaultBranch, logger, ct),
            null,
            new[] { typeof(ClaimedIssue) },
            new[] { typeof(WorktreeReady) })
    {
        _worktrees = worktrees;
        _defaultBranch = defaultBranch;
        _logger = logger;
    }

    public static async ValueTask<WorktreeReady> HandleAsync(
        ClaimedIssue input,
        GitWorktreeService worktrees,
        string defaultBranch,
        ILogger logger,
        CancellationToken ct)
    {
        if (input.Result == ClaimResult.AlreadyClaimed)
        {
            // The workflow edge should route AlreadyClaimed to a sink,
            // so reaching here means someone wired the workflow wrong.
            // Don't throw — log and return a sentinel.
            logger.LogWarning("WorktreeExecutor received AlreadyClaimed for {Id}", input.Issue.Id);
            return new WorktreeReady(input, WorktreeResult.AlreadyClaimed, null, defaultBranch);
        }
        var worktreePath = await worktrees.CreateAsync(input.Issue.Id, defaultBranch, ct);
        return new WorktreeReady(input, WorktreeResult.Ok, worktreePath, defaultBranch);
    }
}

public enum WorktreeResult
{
    Ok,
    AlreadyClaimed,
}

/// <summary>
/// Output of <see cref="WorktreeExecutor"/>. Carries the claim
/// output (so downstream executors can read IssueRecord + branch)
/// plus the freshly-created worktree path.
/// </summary>
public sealed record WorktreeReady(
    ClaimedIssue Claim,
    WorktreeResult Result,
    string? WorktreePath,
    string BaseBranch);