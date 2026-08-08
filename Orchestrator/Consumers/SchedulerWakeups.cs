namespace Forge.Orchestrator.Consumers;

/// <summary>
/// The process-wide wakeup signals, one per event-driven loop. Held in
/// a single record because multiple WakeupSignal instances can't be
/// registered distinctly in DI. Consumers signal; the loops (dispatch,
/// groomer, designer, artist, assembler) race a wait against their
/// 15-minute backstop.
/// </summary>
public sealed record SchedulerWakeups(
    WakeupSignal Dispatch,
    WakeupSignal Groom,
    WakeupSignal Design,
    WakeupSignal Artist,
    WakeupSignal Assemble)
{
    public static SchedulerWakeups Create() => new(
        new WakeupSignal(), new WakeupSignal(), new WakeupSignal(), new WakeupSignal(), new WakeupSignal());

    public WakeupSignal For(Core.Messaging.SweepKind kind) => kind switch
    {
        Core.Messaging.SweepKind.Watch => Dispatch, // unused: watch sweeps run inline in the consumer
        Core.Messaging.SweepKind.Groom => Groom,
        Core.Messaging.SweepKind.Design => Design,
        Core.Messaging.SweepKind.Artist => Artist,
        Core.Messaging.SweepKind.Assemble => Assemble,
        _ => Dispatch,
    };
}
