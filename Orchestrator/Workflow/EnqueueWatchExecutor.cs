using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Orchestrator.Workflow;

/// <summary>
/// Final executor in the engineering dispatch workflow. Enqueues
/// a pr-watch issue (handled separately by PRWatcher) so the PR
/// state is monitored and the issue is moved through the review
/// loop. Returns a <see cref="WatchEnqueued"/> with the watch
/// issue id.
/// </summary>
public sealed class EnqueueWatchExecutor : FunctionExecutor<PrOpened, WatchEnqueued>
{
    private readonly IIssueStore _issues;
    private readonly ILogger<EnqueueWatchExecutor> _logger;

    public EnqueueWatchExecutor(IIssueStore issues, ILogger<EnqueueWatchExecutor> logger)
        : base(
            "enqueue-watch",
            (input, ctx, ct) => HandleAsync(input, issues, logger, ct),
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
        var dev = input.Agent.Worktree.Claim.Issue;
        var branch = input.Agent.Worktree.Claim.Branch ?? $"agent/{dev.Id}";
        var worktreePath = input.Agent.Worktree.WorktreePath!;
        var watch = await issues.CreateAsync(new NewIssue(
            Type: AgentTaskTypes.PrWatch,
            Title: $"Watch PR #{input.PrNumber} for {dev.Id}",
            Description: $"Wait for PR #{input.PrNumber} to be reviewed.",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = input.PrNumber,
                ["branch"] = branch,
                ["worktreePath"] = worktreePath,
                ["taskId"] = dev.Id,
            }), ct);
        logger.LogInformation("Enqueued watch issue {Id} for PR #{Pr}", watch.Id, input.PrNumber);
        return new WatchEnqueued(input, watch.Id);
    }
}

public sealed record WatchEnqueued(PrOpened Pr, string? WatchIssueId);