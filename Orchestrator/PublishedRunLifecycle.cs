using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>Reconcile completed publication through the existing lifecycle event, before review can race it.</summary>
internal static class PublishedRunLifecycle
{
    // Stable stripes coordinate distinct store wrappers for the same project/task.
    // Fixed size avoids retaining one lock per historical issue; collisions only
    // serialize this short database-only reconciliation, never agent or HTTP work.
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal static async Task<IssueRecord> ReconcileAsync(IIssueStore issues, TaskStateMachine? lifecycle,
        IssueRecord observed, CancellationToken ct)
    {
        var key = ((issues as IssueStore)?.ProjectId ?? "") + "/" + observed.Id;
        var stripe = Stripes[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)Stripes.Length];
        await stripe.WaitAsync(ct);
        try
        {
            return await ReconcileOwnedAsync(issues, lifecycle, observed, ct);
        }
        finally { stripe.Release(); }
    }

    private static async Task<IssueRecord> ReconcileOwnedAsync(IIssueStore issues, TaskStateMachine? lifecycle,
        IssueRecord observed, CancellationToken ct)
    {
        var current = await issues.GetAsync(observed.Id, ct) ?? observed;
        if (lifecycle is null || current.Status != IssueStatus.InProgress
            || current.DispatchCheckpoint is not >= DispatchCheckpoint.PushDone
            || !int.TryParse(current.GetMetadata("prNumber"), out var number) || number <= 0
            || !Enum.TryParse<TaskLifecycleState>(current.GetMetadata("state"), out var state)
            || state is not (TaskLifecycleState.AgentRunning or TaskLifecycleState.ReworkRunning
                or TaskLifecycleState.Dispatching or TaskLifecycleState.ReworkQueued))
            return current;

        // Claim clears the prior dispatch's checkpoint. PushDone plus a PR
        // therefore observes this run's verified publication, including rework
        // to an existing PR. It is not an instruction to run again.
        await lifecycle.ReportAsync(issues, current, TaskEvent.RunCompletedDiff,
            watch: null, hasActiveDevRun: false, ct);
        return await issues.GetAsync(current.Id, ct) ?? current;
    }
}
