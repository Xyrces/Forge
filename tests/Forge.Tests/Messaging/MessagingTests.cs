using System.Text.Json;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Messaging;
using Forge.Orchestrator;
using Forge.Orchestrator.Consumers;
using Microsoft.Extensions.Logging.Abstractions;
using Talaria.Core.Abstractions;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests.Messaging;

/// <summary>Event contracts round-trip through System.Text.Json (the
/// transport's wire format).</summary>
public sealed class EventContractTests
{
    private static void RoundTrips<T>(T evt) where T : IForgeEvent
    {
        var json = JsonSerializer.Serialize(evt);
        var back = JsonSerializer.Deserialize<T>(json);
        Assert.Equal(evt, back);
    }

    [Fact]
    public void TaskEnqueued_RoundTrips() => RoundTrips(new TaskEnqueued
    {
        MessageId = "enqueued:task-1:2026-08-08T00:00:00.0000000+00:00",
        ProjectId = "proj", TaskId = "task-1", TaskType = "dev",
        EnqueuedAt = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
    });

    [Fact]
    public void TaskTransitioned_RoundTrips() => RoundTrips(new TaskTransitioned
    {
        MessageId = "transition:task-1:MergeReady:2026-08-08T00:00:00.0000000+00:00",
        ProjectId = "proj", TaskId = "task-1",
        FromState = TaskLifecycleState.PROpen, ToState = TaskLifecycleState.MergeReady,
        StateEnteredAt = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
    });

    [Fact]
    public void PrOpened_RoundTrips() => RoundTrips(new PrOpened
    {
        MessageId = "pr-opened:task-1:42:abc", ProjectId = "proj", TaskId = "task-1",
        PrNumber = 42, Branch = "agent/task-1",
    });

    [Fact]
    public void ReviewVerdictRecorded_RoundTrips() => RoundTrips(new ReviewVerdictRecorded
    {
        MessageId = "review-verdict:task-1:42:abc:1", ProjectId = "proj", TaskId = "task-1",
        PrNumber = 42, Verdict = "Approve", ReviewSha = "abc", ReviewRound = 1,
    });

    [Fact]
    public void SpecStatusChanged_RoundTrips() => RoundTrips(new SpecStatusChanged
    {
        MessageId = "spec-status:spec-1:Approved:2026-08-08T00:00:00.0000000+00:00",
        ProjectId = "proj", SpecId = "spec-1", FromStatus = "Draft", ToStatus = "Approved",
    });

    [Fact]
    public void SprintStatusChanged_RoundTrips() => RoundTrips(new SprintStatusChanged
    {
        MessageId = "sprint-status:sprint-1:Completed:2026-08-08T00:00:00.0000000+00:00",
        ProjectId = "proj", SprintId = "sprint-1", FromStatus = "Active", ToStatus = "Completed",
    });

    [Fact]
    public void FollowUpFiled_RoundTrips() => RoundTrips(new FollowUpFiled
    {
        MessageId = "followup:7", ProjectId = "proj", FollowUpId = 7,
        FollowUpOfTaskId = "task-1", Title = "follow-up",
    });

    [Fact]
    public void GroomRequested_RoundTrips() => RoundTrips(new GroomRequested
    {
        MessageId = "groom:proj:spec-1:-:2026-08-08T00:00:00.0000000+00:00",
        ProjectId = "proj", SpecId = "spec-1",
    });

    [Fact]
    public void SweepTick_RoundTrips() => RoundTrips(new SweepTick
    {
        MessageId = "sweep:Watch:proj:2026-08-08T00:00:00.0000000+00:00",
        ProjectId = "proj", Kind = SweepKind.Watch,
    });

    [Fact]
    public void DeterministicIds_AreStable()
    {
        var at = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TaskEnqueued.IdFor("task-1", at), TaskEnqueued.IdFor("task-1", at));
        Assert.NotEqual(TaskEnqueued.IdFor("task-1", at), TaskEnqueued.IdFor("task-2", at));
        Assert.Equal(
            TaskTransitioned.IdFor("task-1", TaskLifecycleState.MergeReady, at),
            TaskTransitioned.IdFor("task-1", TaskLifecycleState.MergeReady, at));
    }

    [Fact]
    public void DeterministicIds_AreProjectQualified()
    {
        // Task ids, follow-up rowids and PR numbers are per-project
        // sequences; without the project qualifier the transport's
        // idempotency dedupe would drop the second project's event as
        // a duplicate on the shared topic.
        Assert.NotEqual(
            PrOpened.IdFor("proj-a", "task-1", 42, "abc"),
            PrOpened.IdFor("proj-b", "task-1", 42, "abc"));
        Assert.NotEqual(
            ReviewVerdictRecorded.IdFor("proj-a", "task-1", 42, "abc", 1),
            ReviewVerdictRecorded.IdFor("proj-b", "task-1", 42, "abc", 1));
        Assert.NotEqual(
            FollowUpFiled.IdFor("proj-a", 7),
            FollowUpFiled.IdFor("proj-b", 7));
        Assert.Equal(
            PrOpened.IdFor("proj-a", "task-1", 42),
            PrOpened.IdFor("proj-a", "task-1", 42));
    }

    [Fact]
    public void Topics_MapEveryContract()
    {
        Assert.Equal(Topics.TaskEnqueued, Topics.For<TaskEnqueued>());
        Assert.Equal(Topics.TaskTransitioned, Topics.For<TaskTransitioned>());
        Assert.Equal(Topics.PrOpened, Topics.For<PrOpened>());
        Assert.Equal(Topics.ReviewVerdictRecorded, Topics.For<ReviewVerdictRecorded>());
        Assert.Equal(Topics.SpecStatusChanged, Topics.For<SpecStatusChanged>());
        Assert.Equal(Topics.SprintStatusChanged, Topics.For<SprintStatusChanged>());
        Assert.Equal(Topics.FollowUpFiled, Topics.For<FollowUpFiled>());
        Assert.Equal(Topics.GroomRequested, Topics.For<GroomRequested>());
        Assert.Equal(Topics.SweepTick, Topics.For<SweepTick>());
    }
}

/// <summary>TalariaEventPublisher over the in-memory transport.</summary>
public sealed class TalariaEventPublisherTests
{
    [Fact]
    public async Task Publish_LandsOnMappedTopic_WithDeterministicMessageId()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var at = DateTimeOffset.UtcNow;
        var evt = new TaskEnqueued
        {
            MessageId = TaskEnqueued.IdFor("task-9", at),
            ProjectId = "proj", TaskId = "task-9", TaskType = "dev", EnqueuedAt = at,
        };

        await publisher.PublishAsync(evt);

        var msgs = await transport.ReadAllFromTopicAsync<TaskEnqueued>(Topics.TaskEnqueued);
        var env = Assert.Single(msgs);
        Assert.Equal("task-9", env.Payload.TaskId);
        Assert.Equal(evt.MessageId, env.Headers.MessageId);
    }

    [Fact]
    public async Task Publish_PublishesPerEventTypeTopic()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);

        await publisher.PublishAsync(new SweepTick
        {
            MessageId = "sweep:Groom:proj:x", ProjectId = "proj", Kind = SweepKind.Groom,
        });

        var msgs = await transport.ReadAllFromTopicAsync<SweepTick>(Topics.SweepTick);
        Assert.Single(msgs);
        Assert.Empty(await transport.ReadAllFromTopicAsync<TaskEnqueued>(Topics.TaskEnqueued));
    }

    [Fact]
    public async Task Publish_Failure_IsSwallowed_AndLogged()
    {
        // A bus hiccup must never break the DB mutation that triggered
        // the event (the 15m backstop re-derives lost hints).
        var publisher = new TalariaEventPublisher(new ThrowingTransport(), NullLogger<TalariaEventPublisher>.Instance);
        var evt = new TaskEnqueued
        {
            MessageId = "enqueued:task-1:x", ProjectId = "proj", TaskId = "task-1",
        };
        await publisher.PublishAsync(evt); // must not throw
    }

    private sealed class ThrowingTransport : ITransport
    {
        public string Name => "Throwing";
        public Task<IConsumer<T>> CreateConsumerAsync<T>(string topic, ConsumerOptions options, CancellationToken ct = default)
            => throw new InvalidOperationException("bus is down");
        public Task<IProducer<T>> CreateProducerAsync<T>(string topic, ProducerOptions options, CancellationToken ct = default)
            => throw new InvalidOperationException("bus is down");
        public Task<ITransactionalSession> BeginTransactionAsync(
            string? consumerGroup = null, TransactionOffsetSource? offsetSource = null, CancellationToken ct = default)
            => throw new InvalidOperationException("bus is down");
    }
}

/// <summary>Consumer commit/nack semantics over the in-memory transport.</summary>
public sealed class EventConsumerTests
{
    private sealed class TestConsumer(
        ITransport transport,
        Func<TaskEnqueued, Task> handle) : EventConsumer<TaskEnqueued>(transport, NullLogger<TestConsumer>.Instance)
    {
        protected override string Topic => "test.consumer";
        protected override TimeSpan InitialBackoff => TimeSpan.FromMilliseconds(50);
        protected override Task HandleAsync(TaskEnqueued evt, CancellationToken ct) => handle(evt);
    }

    /// <summary>Fails CreateConsumerAsync a fixed number of times, then
    /// delegates to the real in-memory transport (transient bus fault).</summary>
    private sealed class FlakyTransport(InMemoryTransport inner, int failuresBeforeSuccess) : ITransport
    {
        private int _failures = failuresBeforeSuccess;
        public string Name => "Flaky";
        public Task<IConsumer<T>> CreateConsumerAsync<T>(string topic, ConsumerOptions options, CancellationToken ct = default)
            => Interlocked.Decrement(ref _failures) >= 0
                ? throw new InvalidOperationException("transient bus fault")
                : inner.CreateConsumerAsync<T>(topic, options, ct);
        public Task<IProducer<T>> CreateProducerAsync<T>(string topic, ProducerOptions options, CancellationToken ct = default)
            => inner.CreateProducerAsync<T>(topic, options, ct);
        public Task<ITransactionalSession> BeginTransactionAsync(
            string? consumerGroup = null, TransactionOffsetSource? offsetSource = null, CancellationToken ct = default)
            => inner.BeginTransactionAsync(consumerGroup, offsetSource, ct);
    }

    private static async Task ProduceAsync(InMemoryTransport transport, string topic, TaskEnqueued evt)
    {
        var producer = await transport.CreateProducerAsync<TaskEnqueued>(topic, new ProducerOptions());
        await producer.ProduceAsync(evt, new MessageHeaders { MessageId = evt.MessageId }, partitionKey: evt.ProjectId);
    }

    [Fact]
    public async Task Handle_Success_Commits()
    {
        var transport = new InMemoryTransport();
        var handled = new TaskCompletionSource<TaskEnqueued>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new TestConsumer(transport, evt =>
        {
            handled.TrySetResult(evt);
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(cts.Token);

        var evt = new TaskEnqueued { MessageId = "enqueued:task-1:x", ProjectId = "proj", TaskId = "task-1" };
        await ProduceAsync(transport, "test.consumer", evt);

        var got = await handled.Task.WaitAsync(cts.Token);
        Assert.Equal("task-1", got.TaskId);
        await WaitForAsync(() => Task.FromResult(consumer.LastHandledAtUtc is not null), cts.Token);
        // Commit is a no-op in memory: nothing redelivers, nothing DLQs.
        Assert.Empty(await transport.ReadAllFromTopicAsync<TaskEnqueued>("test.consumer.dlq"));
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Handle_Fault_Nacks_ToDlq()
    {
        var transport = new InMemoryTransport();
        var attempts = 0;
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new TestConsumer(transport, _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                faulted.TrySetResult();
                throw new InvalidOperationException("handler blew up");
            }
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(cts.Token);

        var evt = new TaskEnqueued { MessageId = "enqueued:task-2:x", ProjectId = "proj", TaskId = "task-2" };
        await ProduceAsync(transport, "test.consumer", evt);

        await faulted.Task.WaitAsync(cts.Token);
        // ReadAllFromTopicAsync DRAINS the channel — read once inside
        // the poll and keep the result.
        List<MessageEnvelope<TaskEnqueued>> dlq = new();
        await WaitForAsync(async () =>
        {
            dlq = await transport.ReadAllFromTopicAsync<TaskEnqueued>("test.consumer.dlq");
            return dlq.Count > 0;
        }, cts.Token);
        Assert.Single(dlq);
        Assert.Equal("task-2", dlq[0].Payload.TaskId);
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TransportFault_RestartsConsumer_AndRecovers()
    {
        // Supervision (PR #90 review): a fault OUTSIDE the per-message
        // handler must not silently kill the topic — the consumer
        // restarts with backoff and picks the stream back up.
        var inner = new InMemoryTransport();
        var transport = new FlakyTransport(inner, failuresBeforeSuccess: 1);
        var handled = new TaskCompletionSource<TaskEnqueued>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new TestConsumer(transport, evt =>
        {
            handled.TrySetResult(evt);
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(cts.Token);

        var evt = new TaskEnqueued { MessageId = "enqueued:task-3:x", ProjectId = "proj", TaskId = "task-3" };
        await ProduceAsync(inner, "test.consumer", evt);

        var got = await handled.Task.WaitAsync(cts.Token);
        Assert.Equal("task-3", got.TaskId);
        await WaitForAsync(() => Task.FromResult(consumer.LastHandledAtUtc is not null), cts.Token);
        await consumer.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<Task<bool>> cond, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await cond()) return;
            await Task.Delay(50, ct);
        }
        Assert.Fail("condition not met before timeout");
    }
}

/// <summary>Stores publish hint events AFTER mutations commit.</summary>
public sealed class StorePublishTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly RecordingPublisher _events = new();
    private readonly IssueStore _issues;

    public StorePublishTests()
    {
        _dir = TempRoot.Instance.NewDirectory("store-publish");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "issues.db");
        _issues = new IssueStore(_dbPath, "proj", _events);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        private readonly List<IForgeEvent> _events = new();
        public IReadOnlyList<IForgeEvent> Events => _events;
        public IEnumerable<T> Of<T>() where T : IForgeEvent => _events.OfType<T>();

        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        {
            lock (_events) _events.Add(evt);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Create_PublishesTaskEnqueued()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "dev", Title: "t", Description: "d"));
        var evt = Assert.Single(_events.Of<TaskEnqueued>());
        Assert.Equal(issue.Id, evt.TaskId);
        Assert.Equal("dev", evt.TaskType);
        Assert.Equal("proj", evt.ProjectId);
        Assert.Equal(TaskEnqueued.IdFor(issue.Id, evt.EnqueuedAt), evt.MessageId);
    }

    [Fact]
    public async Task Transition_WithLifecycleStateChange_PublishesTaskTransitioned()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "dev", Title: "t"));
        var enteredAt = DateTimeOffset.UtcNow;
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object>
            {
                ["state"] = "Dispatching",
                ["stateEnteredAt"] = enteredAt.ToString("O"),
            });

        var evt = Assert.Single(_events.Of<TaskTransitioned>());
        Assert.Equal(issue.Id, evt.TaskId);
        Assert.Equal(TaskLifecycleState.Pending, evt.FromState);
        Assert.Equal(TaskLifecycleState.Dispatching, evt.ToState);
        Assert.Equal(TaskTransitioned.IdFor(issue.Id, TaskLifecycleState.Dispatching, evt.StateEnteredAt), evt.MessageId);
    }

    [Fact]
    public async Task Transition_WithoutLifecycleState_PublishesNothing()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "dev", Title: "t"));
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object> { ["prNumber"] = 42 });
        Assert.Empty(_events.Of<TaskTransitioned>());
    }

    [Fact]
    public async Task SprintStore_PublishesSprintStatusChanged()
    {
        var sprints = new SprintStore(_issues);
        var sprint = await sprints.CreateAsync(new NewSprint(
            Name: "s", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(1),
            Status: SprintStatus.Active));

        var created = Assert.Single(_events.Of<SprintStatusChanged>());
        Assert.Equal(sprint.Id, created.SprintId);
        Assert.Equal("Active", created.ToStatus);
        Assert.Equal("proj", created.ProjectId);

        // Idempotent re-activation: no duplicate event.
        await sprints.SetActiveAsync(sprint.Id);
        Assert.Single(_events.Of<SprintStatusChanged>());

        await sprints.UpdateAsync(sprint.Id, new Dictionary<string, object?> { ["status"] = "Completed" });
        var completed = Assert.Single(_events.Of<SprintStatusChanged>().Where(e => e.ToStatus == "Completed"));
        Assert.Equal(sprint.Id, completed.SprintId);
        Assert.Equal("Active", completed.FromStatus);
    }

    [Fact]
    public async Task SpecStore_PublishesSpecStatusChanged()
    {
        var specs = new SpecStore(_issues);
        var spec = await specs.CreateAsync(new NewSpec(ProjectId: "proj", Title: "t", Body: "b"));

        await specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);

        var evt = Assert.Single(_events.Of<SpecStatusChanged>());
        Assert.Equal(spec.Id, evt.SpecId);
        Assert.Equal("Draft", evt.FromStatus);
        Assert.Equal("ReadyForDesign", evt.ToStatus);
        Assert.Equal("proj", evt.ProjectId);
    }

    [Fact]
    public async Task FollowUpDraftStore_PublishesFollowUpFiled()
    {
        var drafts = new FollowUpDraftStore(_issues);
        var id = await drafts.FileAsync(new FollowUpDraft(
            0, "sprint-1", "task-1", "coredev", "title", "desc", 2, null, DateTime.UtcNow, null));

        var evt = Assert.Single(_events.Of<FollowUpFiled>());
        Assert.Equal(id, evt.FollowUpId);
        Assert.Equal("task-1", evt.FollowUpOfTaskId);
        Assert.Equal("proj", evt.ProjectId);
    }

    [Fact]
    public async Task NullPublisher_StoresStaySilent()
    {
        await using var plain = new IssueStore(Path.Combine(_dir, "plain.db"));
        await plain.CreateAsync(new NewIssue(Type: "dev", Title: "t"));
        // Default ctor = NullEventPublisher: nothing to assert beyond
        // no-throw, which CreateAsync already proves.
    }
}

/// <summary>Coalescing wakeup semantics.</summary>
public sealed class WakeupSignalTests
{
    [Fact]
    public async Task Signal_CompletesWait()
    {
        var signal = new WakeupSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        signal.Signal();
        await signal.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task NoSignal_WaitBlocks()
    {
        var signal = new WakeupSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var wait = signal.WaitAsync(cts.Token).AsTask();
        var done = await Task.WhenAny(wait, Task.Delay(150));
        Assert.NotSame(wait, done);
        try { await wait; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DoubleSignal_CoalescesToOnePendingWakeup()
    {
        var signal = new WakeupSignal();
        signal.Signal();
        signal.Signal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await signal.WaitAsync(cts.Token); // drains the single pending wakeup
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await signal.WaitAsync(cts2.Token));
    }
}

/// <summary>End-to-end: store mutation → bus → consumer → loop wakeup,
/// no timer involved.</summary>
public sealed class EnqueueWakeupEndToEndTests : IDisposable
{
    private readonly string _dir;

    public EnqueueWakeupEndToEndTests()
    {
        _dir = TempRoot.Instance.NewDirectory("enqueue-e2e");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EnqueueTask_PublishesEvent_ConsumerWakesDispatch()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var wakeups = SchedulerWakeups.Create();
        var consumer = new TaskEnqueuedConsumer(
            transport, wakeups, NullLogger<TaskEnqueuedConsumer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(cts.Token);

        await using var issues = new IssueStore(
            Path.Combine(_dir, "issues.db"), "proj", publisher);
        var issue = await issues.CreateAsync(new NewIssue(Type: "dev", Title: "real work"));

        // The dispatch wakeup fires without any timer: the store's
        // TaskEnqueued traveled the bus to the consumer's kick.
        await wakeups.Dispatch.WaitAsync(cts.Token);
        await wakeups.Assemble.WaitAsync(cts.Token);
        // LastHandledAtUtc is set after Commit — poll the tiny window.
        while (consumer.LastHandledAtUtc is null && !cts.IsCancellationRequested)
            await Task.Delay(25, cts.Token);
        Assert.True(consumer.LastHandledAtUtc is not null);
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LifecycleTransition_PublishesEvent_ConsumerWakesDispatch()
    {
        var transport = new InMemoryTransport();
        var publisher = new TalariaEventPublisher(transport, NullLogger<TalariaEventPublisher>.Instance);
        var wakeups = SchedulerWakeups.Create();
        // TaskTransitionedConsumer needs the bundle factory only for the
        // MergeReady PR poll; a Dispatching transition never reaches it.
        var bundleFactory = new NullBundleFactory();
        var projectStore = new ProjectStore(new IssueStore(Path.Combine(_dir, "registry.db")));
        var consumer = new TaskTransitionedConsumer(
            transport, wakeups, bundleFactory, projectStore,
            NullLogger<TaskTransitionedConsumer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await consumer.StartAsync(cts.Token);

        await using var issues = new IssueStore(
            Path.Combine(_dir, "issues2.db"), "proj", publisher);
        var issue = await issues.CreateAsync(new NewIssue(Type: "dev", Title: "real work"));
        await issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object>
            {
                ["state"] = "Dispatching",
                ["stateEnteredAt"] = DateTimeOffset.UtcNow.ToString("O"),
            });

        await wakeups.Dispatch.WaitAsync(cts.Token);
        await consumer.StopAsync(CancellationToken.None);
    }

    private sealed class NullBundleFactory : IProjectDispatchBundleFactory
    {
        public ProjectDispatchBundle Build(Configuration.ProjectOptions project)
            => throw new InvalidOperationException("no bundles in this test");
    }
}
