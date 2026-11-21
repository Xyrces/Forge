using Forge.Core.Messaging;
using Forge.Messaging;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>Wake after the queue row commits, even when lifecycle was reported earlier.</summary>
public sealed class TaskStatusChangedConsumer(ITransport transport, SchedulerWakeups wakeups,
    ILogger<TaskStatusChangedConsumer> logger) : EventConsumer<TaskStatusChanged>(transport, logger)
{
    protected override Task HandleAsync(TaskStatusChanged evt, CancellationToken ct)
    {
        wakeups.Assemble.Signal();
        wakeups.Dispatch.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>Acceptance and explicit abandonment release the previous sprint's planning barrier.</summary>
public sealed class SprintOutcomeChangedConsumer(ITransport transport, SchedulerWakeups wakeups,
    ILogger<SprintOutcomeChangedConsumer> logger) : EventConsumer<SprintOutcomeChanged>(transport, logger)
{
    protected override Task HandleAsync(SprintOutcomeChanged evt, CancellationToken ct)
    {
        wakeups.Assemble.Signal();
        wakeups.Groom.Signal();
        wakeups.Deploy.Signal();
        return Task.CompletedTask;
    }
}
