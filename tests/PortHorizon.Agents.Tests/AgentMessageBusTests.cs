using PortHorizon.Agents.Orchestrator;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class AgentMessageBusTests
{
    [Fact]
    public void Enqueue_ThenDrain_ReturnsAllMessages()
    {
        var bus = new AgentMessageBus();
        bus.Enqueue("coredev", "hello");
        bus.Enqueue("coredev", "world");
        var drained = bus.Drain("coredev");
        Assert.Equal("hello" + System.Environment.NewLine + "world", drained);
    }

    [Fact]
    public void Drain_AfterRead_ReturnsEmpty()
    {
        var bus = new AgentMessageBus();
        bus.Enqueue("qa", "ping");
        bus.Drain("qa");
        Assert.Equal(string.Empty, bus.Drain("qa"));
    }

    [Fact]
    public void Count_ReflectsPending()
    {
        var bus = new AgentMessageBus();
        Assert.Equal(0, bus.Count("coredev"));
        bus.Enqueue("coredev", "a");
        bus.Enqueue("coredev", "b");
        Assert.Equal(2, bus.Count("coredev"));
        bus.Drain("coredev");
        Assert.Equal(0, bus.Count("coredev"));
    }

    [Fact]
    public void Drain_UnknownAgent_ReturnsEmpty()
    {
        var bus = new AgentMessageBus();
        Assert.Equal(string.Empty, bus.Drain("never-seen"));
    }
}
