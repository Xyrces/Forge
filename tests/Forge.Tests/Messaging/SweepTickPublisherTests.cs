using System.Collections.Concurrent;
using Forge.Core.Messaging;
using Forge.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Messaging;

public sealed class SweepTickPublisherTests
{
    [Fact]
    public async Task StartupIsImmediate_WatchAloneRunsEachMinute_FullBackstopRemainsFifteenMinutes()
    {
        var clock = new ManualClock();
        var publisher = new Recorder();
        using var service = Create(publisher, clock);
        await service.StartAsync(default);
        await publisher.WaitForCountAsync(10);
        AssertAllKinds(publisher.Events.ToArray(), clock.GetUtcNow());
        for (var minute = 1; minute < 15; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await publisher.WaitForCountAsync(10 + minute * 2);
            Assert.All(publisher.Events.TakeLast(2), e => Assert.Equal(SweepKind.Watch, e.Kind));
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        await publisher.WaitForCountAsync(48);
        AssertAllKinds(publisher.Events.TakeLast(10).ToArray(), clock.GetUtcNow());
        Assert.Equal(48, publisher.Events.Select(e => e.MessageId).Distinct().Count());
        await service.StopAsync(default);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(48, publisher.Events.Count);
    }

    [Fact]
    public async Task MissedIntervalsCoalesceWithoutReplayingPlanningOrWatchTicks()
    {
        var clock = new ManualClock();
        var publisher = new Recorder();
        using var service = Create(publisher, clock);
        await service.StartAsync(default);
        await publisher.WaitForCountAsync(10);
        clock.Advance(TimeSpan.FromHours(1));
        await publisher.WaitForCountAsync(20);
        AssertAllKinds(publisher.Events.TakeLast(10).ToArray(), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(1));
        await publisher.WaitForCountAsync(22);
        Assert.All(publisher.Events.TakeLast(2), e => Assert.Equal(SweepKind.Watch, e.Kind));
        await service.StopAsync(default);
    }

    [Fact]
    public async Task EnumerationFailureDoesNotKillSubsequentWatchOrBackstop()
    {
        var clock = new ManualClock();
        var publisher = new Recorder();
        var enumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = Create(publisher, clock, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                enumerated.SetResult();
                throw new InvalidOperationException("Registry unavailable");
            }
            return Task.FromResult<IReadOnlyList<string>>(["a", "b"]);
        });
        await service.StartAsync(default);
        await enumerated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(1));
        await publisher.WaitForCountAsync(2);
        Assert.All(publisher.Events, e => Assert.Equal(SweepKind.Watch, e.Kind));
        clock.Advance(TimeSpan.FromMinutes(14));
        await publisher.WaitForCountAsync(12);
        AssertAllKinds(publisher.Events.TakeLast(10).ToArray(), clock.GetUtcNow());
        await service.StopAsync(default);
    }

    [Fact]
    public async Task PublicationFailureDoesNotSuppressOtherKindsProjectsOrNextPoll()
    {
        var clock = new ManualClock();
        var publisher = new Recorder { FailFirst = true };
        using var service = Create(publisher, clock);
        await service.StartAsync(default);
        await publisher.WaitForCountAsync(9);
        Assert.Contains(publisher.Events, e => e.ProjectId == "b" && e.Kind == SweepKind.Watch);
        clock.Advance(TimeSpan.FromMinutes(1));
        await publisher.WaitForCountAsync(11);
        Assert.All(publisher.Events.TakeLast(2), e => Assert.Equal(SweepKind.Watch, e.Kind));
        await service.StopAsync(default);
    }

    [Fact]
    public async Task ShutdownCancelsInFlightEnumerationWithoutPublishing()
    {
        var clock = new ManualClock();
        var publisher = new Recorder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(publisher, clock, async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ["a", "b"];
        });
        await service.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(publisher.Events);
    }

    private static SweepTickPublisher Create(Recorder publisher, ManualClock clock,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? projects = null) =>
        new(publisher, projects ?? (_ => Task.FromResult<IReadOnlyList<string>>(["a", "b"])),
            NullLogger<SweepTickPublisher>.Instance, clock);

    private static void AssertAllKinds(SweepTick[] events, DateTimeOffset at)
    {
        Assert.Equal(10, events.Length);
        foreach (var project in new[] { "a", "b" })
            Assert.Equal(Enum.GetValues<SweepKind>(), events.Where(e => e.ProjectId == project).Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal(at, e.TickAt));
    }

    private sealed class Recorder : IEventPublisher
    {
        public ConcurrentQueue<SweepTick> Events { get; } = new();
        private readonly SemaphoreSlim _changed = new(0);
        public bool FailFirst { get; init; }
        private int _calls;
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        {
            if (FailFirst && Interlocked.Increment(ref _calls) == 1) throw new InvalidOperationException("Transport unavailable");
            Events.Enqueue(Assert.IsType<SweepTick>(evt));
            _changed.Release();
            return Task.CompletedTask;
        }
        public async Task WaitForCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Events.Count < count) await _changed.WaitAsync(timeout.Token);
            Assert.Equal(count, Events.Count);
        }
    }

    // Advances one PeriodicTimer deterministically. Like PeriodicTimer itself,
    // a large jump emits one notification, not one queued callback per missed minute.
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private ManualTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new ManualTimer(callback, state, GetTimestamp() + dueTime.Ticks, period.Ticks);
        public void Advance(TimeSpan interval)
        {
            var now = Interlocked.Add(ref _ticks, interval.Ticks);
            _timer?.Fire(now);
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, long next, long period) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan interval) => throw new NotSupportedException();
            public void Fire(long now)
            {
                if (_disposed || now < next) return;
                next = now + period;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
