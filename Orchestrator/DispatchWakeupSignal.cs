using System.Threading.Channels;

namespace Forge.Orchestrator;

/// <summary>
/// Event-signaled wakeup for the dispatch loop. Producers (message
/// consumers reacting to TaskEnqueued / TaskTransitioned, and the
/// dispatch loop itself when a run finishes and frees a role slot)
/// signal; the loop races one wait against its 15-minute backstop.
/// Capacity 1 + DropWrite: a pending wakeup is a wakeup, duplicates
/// carry no information.
/// </summary>
public sealed class DispatchWakeupSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Signal() => _channel.Writer.TryWrite(0);

    public ValueTask<byte> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
