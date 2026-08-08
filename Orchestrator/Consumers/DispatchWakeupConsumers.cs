using Forge.Core.Messaging;
using Forge.Messaging;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Consumers;

/// <summary>
/// TaskEnqueued → dispatch-loop wakeup. The handler only kicks (the
/// loop re-reads the ready queue); it does no dispatch work itself.
/// </summary>
public sealed class TaskEnqueuedWakeupConsumer : EventConsumer<TaskEnqueued>
{
    private readonly DispatchWakeupSignal _wakeup;

    public TaskEnqueuedWakeupConsumer(
        Talaria.Core.Abstractions.ITransport transport,
        DispatchWakeupSignal wakeup,
        ILogger<TaskEnqueuedWakeupConsumer> logger)
        : base(transport, logger)
    {
        _wakeup = wakeup;
    }

    protected override Task HandleAsync(TaskEnqueued evt, CancellationToken ct)
    {
        _wakeup.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>
/// TaskTransitioned → dispatch-loop wakeup (a transition can make work
/// claimable: requeues, unblocks, sprint-member state changes).
/// </summary>
public sealed class TaskTransitionedWakeupConsumer : EventConsumer<TaskTransitioned>
{
    private readonly DispatchWakeupSignal _wakeup;

    public TaskTransitionedWakeupConsumer(
        Talaria.Core.Abstractions.ITransport transport,
        DispatchWakeupSignal wakeup,
        ILogger<TaskTransitionedWakeupConsumer> logger)
        : base(transport, logger)
    {
        _wakeup = wakeup;
    }

    protected override Task HandleAsync(TaskTransitioned evt, CancellationToken ct)
    {
        _wakeup.Signal();
        return Task.CompletedTask;
    }
}
