using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Orchestrator.Workflow;

/// <summary>
/// First executor in the engineering dispatch workflow. Claims a
/// Pending issue via <see cref="IIssueStore.ClaimAsync"/> so two
/// orchestrators can't grab the same issue. Outputs a
/// <see cref="ClaimedIssue"/> carrying the post-claim record plus
/// the resolved branch (worktreePath is set by a later executor).
/// </summary>
public sealed class ClaimExecutor : FunctionExecutor<IssueRecord, ClaimedIssue>
{
    private readonly IIssueStore _issues;
    private readonly ILogger<ClaimExecutor> _logger;

    public ClaimExecutor(IIssueStore issues, ILogger<ClaimExecutor> logger)
        : base(
            "claim",
            (input, ctx, ct) => HandleAsync(input, issues, logger, ct),
            null,
            new[] { typeof(IssueRecord) },
            new[] { typeof(ClaimedIssue) })
    {
        _issues = issues;
        _logger = logger;
    }

    /// <summary>
    /// Public static so tests can drive the executor's logic
    /// without spinning up a WorkflowHost. The runtime invokes
    /// this through the same delegate passed to the base ctor.
    /// </summary>
    public static async ValueTask<ClaimedIssue> HandleAsync(
        IssueRecord input,
        IIssueStore issues,
        ILogger logger,
        CancellationToken ct)
    {
        var claimed = await issues.ClaimAsync(input.Id, "kilo", ct);
        if (claimed is null)
        {
            logger.LogDebug("Issue {Id} already claimed elsewhere", input.Id);
            return new ClaimedIssue(input, ClaimResult.AlreadyClaimed, null, null);
        }
        var branch = claimed.GetMetadata("branch") ?? $"agent/{claimed.Id}";
        return new ClaimedIssue(claimed, ClaimResult.Ok, null, branch);
    }
}

public enum ClaimResult
{
    Ok,
    AlreadyClaimed,
}

/// <summary>
/// Output of <see cref="ClaimExecutor"/>. Carries the original
/// IssueRecord, the post-claim record (same as original on success),
/// and the resolved branch. WorktreePath is populated by the
/// worktree executor that follows downstream.
/// </summary>
public sealed record ClaimedIssue(
    IssueRecord Issue,
    ClaimResult Result,
    string? WorktreePath,
    string? Branch);