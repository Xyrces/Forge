using Forge.Core.Messaging;
using Forge.Messaging;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>
/// TaskEnqueued → kick the dispatch loop (new work may be claimable),
/// the sprint assembler (ad-hoc work may need ingest), AND the groomer
/// (a freshly created parentless task — e.g. a materialized sprint
/// follow-up — is born ungroomed; observed live 2026-08-08: sprint
/// assembly stalled 15m in awaiting-groom because materialization
/// published nothing the groomer listens to). Kicks only — the loops
/// re-read DB truth. No spin risk: a groom tick that finds no
/// ungroomed ad-hoc work publishes nothing.
/// </summary>
public sealed class TaskEnqueuedConsumer : EventConsumer<TaskEnqueued>
{
    private readonly SchedulerWakeups _wakeups;

    public TaskEnqueuedConsumer(
        ITransport transport,
        SchedulerWakeups wakeups,
        ILogger<TaskEnqueuedConsumer> logger)
        : base(transport, logger)
    {
        _wakeups = wakeups;
    }

    protected override Task HandleAsync(TaskEnqueued evt, CancellationToken ct)
    {
        _wakeups.Dispatch.Signal();
        _wakeups.Assemble.Signal();
        _wakeups.Groom.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>
/// TaskTransitioned → kick dispatch + assembler (requeues, unblocks,
/// terminal states), and poll the PR immediately when the task entered
/// MergeReady (CI-green determination is GitHub truth; don't wait for
/// the sweep).
/// </summary>
public sealed class TaskTransitionedConsumer : WatchConsumerBase<TaskTransitioned>
{
    private readonly SchedulerWakeups _wakeups;
    private readonly ILogger<TaskTransitionedConsumer> _logger;

    public TaskTransitionedConsumer(
        ITransport transport,
        SchedulerWakeups wakeups,
        IProjectDispatchBundleFactory bundleFactory,
        Core.IProjectStore projectStore,
        ILogger<TaskTransitionedConsumer> logger)
        : base(transport, bundleFactory, projectStore, logger)
    {
        _wakeups = wakeups;
        _logger = logger;
    }

    protected override async Task HandleAsync(TaskTransitioned evt, CancellationToken ct)
    {
        _wakeups.Dispatch.Signal();
        _wakeups.Assemble.Signal();

        if (evt.ToState != Core.TaskLifecycleState.MergeReady) return;
        var bundle = await BundleForAsync(evt.ProjectId, _logger, ct);
        if (bundle is null) return;
        var task = await bundle.IssueStore.GetAsync(evt.TaskId, ct);
        if (task is null || task.GetMetadata("prNumber") is null) return;
        try
        {
            var outcome = await bundle.PrWatcher.PollWatchedTaskAsync(task, ct);
            _logger.LogInformation("MergeReady-driven poll for {TaskId}: {Outcome}", evt.TaskId, outcome);
        }
        catch (Octokit.RateLimitExceededException)
        {
            _logger.LogWarning("MergeReady-driven poll for {TaskId} hit the GitHub rate limit — sweep backstop covers", evt.TaskId);
        }
    }
}
