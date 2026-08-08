using System.Threading.Channels;

namespace Forge.Orchestrator;

/// <summary>
/// Coalescing wakeup for event-driven loops (dispatch, schedulers).
/// Message consumers signal (kicks only — the loop does the work in
/// its own RunAsync); the loop races one wait against its backstop
/// interval. Capacity 1 + DropWrite: a pending wakeup is a wakeup,
/// duplicates carry no information.
/// </summary>
public sealed class WakeupSignal
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
