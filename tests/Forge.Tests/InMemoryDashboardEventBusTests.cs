using Forge.Dashboard;
using Xunit;

namespace Forge.Tests;

public class InMemoryDashboardEventBusTests
{
    [Fact]
    public async Task Publish_IsReceivedBySubscriber()
    {
        var bus = new InMemoryDashboardEventBus();
        var reader = bus.Subscribe();

        bus.Publish(new DashboardEvent(DateTime.UtcNow, "test.kind", "task-1", "hello"));

        var got = await reader.ReadAsync();
        Assert.Equal("test.kind", got.Kind);
        Assert.Equal("task-1", got.TaskId);
        Assert.Equal("hello", got.Detail);
    }

    [Fact]
    public async Task NewSubscriber_GetsHistorySnapshot()
    {
        var bus = new InMemoryDashboardEventBus();
        bus.Publish(new DashboardEvent(DateTime.UtcNow, "first", null, null));
        bus.Publish(new DashboardEvent(DateTime.UtcNow, "second", null, null));

        var reader = bus.Subscribe();
        var kinds = new List<string>();
        for (var i = 0; i < 2; i++)
            kinds.Add((await reader.ReadAsync()).Kind);

        Assert.Equal(new[] { "first", "second" }, kinds);
    }

    [Fact]
    public async Task MultipleSubscribers_BothReceiveEvents()
    {
        var bus = new InMemoryDashboardEventBus();
        var r1 = bus.Subscribe();
        var r2 = bus.Subscribe();
        bus.Publish(new DashboardEvent(DateTime.UtcNow, "x", null, null));

        Assert.Equal("x", (await r1.ReadAsync()).Kind);
        Assert.Equal("x", (await r2.ReadAsync()).Kind);
    }

    [Fact]
    public async Task HistoryBuffer_TrimsToMaxBuffered()
    {
        var bus = new InMemoryDashboardEventBus();
        for (var i = 0; i < 1100; i++)
            bus.Publish(new DashboardEvent(DateTime.UtcNow, $"k{i}", null, null));

        var reader = bus.Subscribe();
        var first = await reader.ReadAsync();
        var last = first;
        for (var i = 1; i < 1024; i++)
            last = await reader.ReadAsync();

        Assert.StartsWith("k76", first.Kind);
        Assert.StartsWith("k1099", last.Kind);
    }
}