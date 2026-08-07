using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Orchestrator.Workflow;

/// <summary>
/// Final executor in the engineering dispatch workflow. Kept as a
/// DAG stage so the (editable) workflow graph keeps its shape; as of
/// 2026-07-29 it creates NO watch row — PR watching is state-driven:
/// the task carries prNumber/branch/worktreePath metadata and the
/// watch sweep polls every live task with a prNumber. Returns a
/// <see cref="WatchEnqueued"/> for graph compatibility.
/// </summary>
public sealed class EnqueueWatchExecutor : FunctionExecutor<PrOpened, WatchEnqueued>
{
    private readonly IIssueStore _issues;
    private readonly ILogger<EnqueueWatchExecutor> _logger;

    public EnqueueWatchExecutor(IIssueStore issues, ILogger<EnqueueWatchExecutor> logger)
        : base(
            "enqueue-watch",
            ExecutorFaultGuard.Wrap<PrOpened, WatchEnqueued>("enqueue-watch", logger, (input, ctx, ct) => HandleAsync(input, issues, logger, ct)),
            null,
            new[] { typeof(PrOpened) },
            new[] { typeof(WatchEnqueued) })
    {
        _issues = issues;
        _logger = logger;
    }

    public static async ValueTask<WatchEnqueued> HandleAsync(
        PrOpened input,
        IIssueStore issues,
        ILogger logger,
        CancellationToken ct)
    {
        if (input.Result != PrResult.Ok)
        {
            return new WatchEnqueued(input, null);
        }
        // No watch row: the sweep discovers watched tasks by their
        // prNumber metadata (written by CommitPushPrExecutor). The
        // stage remains so the workflow DAG's edges stay valid.
        var dev = input.Agent.Worktree.Claim.Issue;
        logger.LogInformation("Watch armed for PR #{Pr} via task {Id} state (no watch row)", input.PrNumber, dev.Id);
        await Task.CompletedTask;
        return new WatchEnqueued(input, null);
    }
}

public sealed record WatchEnqueued(PrOpened Pr, string? WatchIssueId);