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
    ///
    /// <para>
    /// When called from the orchestrator's pre-claim path, the
    /// input is already <see cref="IssueStatus.InProgress"/> with
    /// assignee = "kilo" — we treat that as a successful claim
    /// and pass through to the worktree stage. When called
    /// directly (e.g. from a workflow that doesn't pre-claim),
    /// this method performs the actual <see cref="IIssueStore.ClaimAsync"/>
    /// and either succeeds or returns
    /// <see cref="ClaimResult.AlreadyClaimed"/> as a first-class
    /// short-circuit signal.
    /// </para>
    /// </summary>
    public static async ValueTask<ClaimedIssue> HandleAsync(
        IssueRecord input,
        IIssueStore issues,
        ILogger logger,
        CancellationToken ct)
    {
        // Pre-claim path: the orchestrator already claimed the
        // issue. Pass through; no re-claim, no AlreadyClaimed.
        if (input.Status == IssueStatus.InProgress && input.Assignee == "kilo")
        {
            var preClaimedBranch = input.GetMetadata("branch") ?? $"agent/{input.Id}";
            return new ClaimedIssue(input, ClaimResult.Ok, null, preClaimedBranch);
        }

        // Standalone path: do the claim ourselves.
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