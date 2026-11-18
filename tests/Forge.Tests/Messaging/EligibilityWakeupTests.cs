using Forge.Core;
using Forge.Core.Messaging;
using Forge.Messaging;
using Forge.Orchestrator.Consumers;
using Microsoft.Extensions.Logging.Abstractions;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests.Messaging;

public sealed class EligibilityWakeupTests
{
    [Fact]
    public async Task GroomedSpec_WakesAssembler_AtActualEligibilityBoundary()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var wakeups = SchedulerWakeups.Create();
        var consumer = new SpecStatusChangedConsumer(transport, wakeups, NullLogger<SpecStatusChangedConsumer>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(timeout.Token);
        try
        {
            await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("groomed-wake"), "project", publisher);
            var specs = new SpecStore(issues);
            var spec = await specs.CreateAsync(new("project", "Spec", "Body"));
            await specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
            await specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
            await specs.SetStatusAsync(spec.Id, SpecStatus.Grooming);
            await specs.SetStatusAsync(spec.Id, SpecStatus.Groomed);
            await wakeups.Assemble.WaitAsync(timeout.Token);
            Assert.Equal(SpecStatus.Groomed, (await specs.GetAsync(spec.Id))!.Status);
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task FinalQueueStatus_WakesAssembler_AfterEarlierLifecycleObservation()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var wakeups = SchedulerWakeups.Create();
        var consumer = new TaskStatusChangedConsumer(transport, wakeups, NullLogger<TaskStatusChangedConsumer>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("completed-wake"), "project", publisher);
        var task = await issues.CreateAsync(new("task", "Last sprint member"));
        // Lifecycle may be recorded before the actual status update. There is
        // deliberately no TaskTransitioned consumer in this test.
        await issues.TransitionAsync(task.Id, IssueStatus.Pending, null,
            new Dictionary<string, object> { ["state"] = "Completed", ["stateEnteredAt"] = DateTimeOffset.UtcNow.ToString("O") });
        await consumer.StartAsync(timeout.Token);
        try
        {
            await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
            await wakeups.Assemble.WaitAsync(timeout.Token);
            await wakeups.Dispatch.WaitAsync(timeout.Token);
            Assert.Equal(IssueStatus.Completed, (await issues.GetAsync(task.Id))!.Status);
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AcceptedOutcome_WakesPlanning_WithoutWaitingForSweep()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var wakeups = SchedulerWakeups.Create();
        var consumer = new SprintOutcomeChangedConsumer(transport, wakeups, NullLogger<SprintOutcomeChangedConsumer>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(timeout.Token);
        try
        {
            await publisher.PublishAsync(new SprintOutcomeChanged
            {
                MessageId = "accepted:project:sprint-1", ProjectId = "project", SprintId = "sprint-1",
                CompletionId = "completion-sprint-1", Outcome = "Satisfied",
            });
            await wakeups.Assemble.WaitAsync(timeout.Token);
            await wakeups.Groom.WaitAsync(timeout.Token);
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }
}
