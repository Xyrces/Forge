using System.Threading.Channels;

namespace PortHorizon.Agents.Dashboard;

public interface IDashboardEventBus
{
    void Publish(DashboardEvent @event);
    ChannelReader<DashboardEvent> Subscribe();
}

public sealed class InMemoryDashboardEventBus : IDashboardEventBus
{
    private const int MaxBuffered = 1024;
    private readonly object _lock = new();
    private readonly List<Channel<DashboardEvent>> _subscribers = new();
    private readonly LinkedList<DashboardEvent> _history = new();
    private int _historyCount;

    public void Publish(DashboardEvent @event)
    {
        Channel<DashboardEvent>[] snapshot;
        lock (_lock)
        {
            _history.AddLast(@event);
            _historyCount++;
            while (_historyCount > MaxBuffered && _history.First is not null)
            {
                _history.RemoveFirst();
                _historyCount--;
            }
            snapshot = _subscribers.ToArray();
        }

        foreach (var sub in snapshot)
        {
            sub.Writer.TryWrite(@event);
        }
    }

    public ChannelReader<DashboardEvent> Subscribe()
    {
        var channel = Channel.CreateUnbounded<DashboardEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        lock (_lock)
        {
            foreach (var past in _history)
                channel.Writer.TryWrite(past);
            _subscribers.Add(channel);
        }
        return channel.Reader;
    }

    public IReadOnlyList<DashboardEvent> GetHistorySnapshot()
    {
        lock (_lock)
            return _history.ToArray();
    }
}