using Forge.Core;
using Forge.Core.Messaging;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>Webhook and UI hints use the same reconciliation as the periodic lost-event backstop.</summary>
public sealed class PrWatchRequestedConsumer(ITransport transport, IProjectDispatchBundleFactory bundles,
    IProjectStore projects, WatchSweepService sweeps, ILogger<PrWatchRequestedConsumer> logger)
    : WatchConsumerBase<PrWatchRequested>(transport, bundles, projects, logger)
{
    protected override async Task HandleAsync(PrWatchRequested evt, CancellationToken ct)
    {
        var bundle = await BundleForAsync(evt.ProjectId, logger, ct);
        if (bundle is null) return;
        // Some check/status payloads omit branch and PR associations. Resolve
        // a base-head signal using current GitHub truth so infrastructure waits
        // wake immediately without guessing from a stale payload.
        if (evt.TaskId is null && evt.PrNumber is null && evt.Branch is null && evt.HeadSha is not null
            && evt.Reason is "github:check_run" or "github:check_suite" or "github:status"
            && await bundle.GitHub.GetBranchHeadShaAsync(bundle.Project.DefaultBranch, ct) == evt.HeadSha)
            evt = evt with { Branch = bundle.Project.DefaultBranch };
        await sweeps.SweepProjectAsync(bundle, ct, task => Matches(evt, task, bundle.Project.DefaultBranch));
    }

    internal static bool Matches(PrWatchRequested evt, IssueRecord task, string defaultBranch)
    {
        if (evt.TaskId is not null) return task.Id == evt.TaskId;
        if (evt.PrNumber is { } number)
            return int.TryParse(task.GetMetadata("prNumber"), out var recorded) && recorded == number;
        // Base pushes/checks change mergeability and infrastructure waits across
        // the project. Feature branch events remain task-scoped.
        if (evt.Branch == defaultBranch) return true;
        return (evt.Branch is not null && task.GetMetadata("branch") == evt.Branch)
            || (evt.HeadSha is not null && task.GetMetadata("branchSha") == evt.HeadSha);
    }
}
