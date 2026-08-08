using Forge.Core.Messaging;
using Forge.Messaging;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>
/// SweepTick → the 15-minute backstop. Watch ticks run the sequential
/// sweep inline (bounded GitHub polls; reviews launch off-loop);
/// groom/design/artist/assemble ticks kick the owning scheduler's
/// wakeup (TickAsync re-derives everything from DB truth).
/// </summary>
public sealed class SweepTickConsumer : WatchConsumerBase<SweepTick>
{
    private readonly SchedulerWakeups _wakeups;
    private readonly WatchSweepService _sweeps;
    private readonly ILogger<SweepTickConsumer> _logger;

    public SweepTickConsumer(
        ITransport transport,
        IProjectDispatchBundleFactory bundleFactory,
        Core.IProjectStore projectStore,
        SchedulerWakeups wakeups,
        WatchSweepService sweeps,
        ILogger<SweepTickConsumer> logger)
        : base(transport, bundleFactory, projectStore, logger)
    {
        _wakeups = wakeups;
        _sweeps = sweeps;
        _logger = logger;
    }

    protected override async Task HandleAsync(SweepTick evt, CancellationToken ct)
    {
        if (evt.Kind == SweepKind.Watch)
        {
            var bundle = await BundleForAsync(evt.ProjectId, _logger, ct);
            if (bundle is null) return;
            await _sweeps.SweepProjectAsync(bundle, ct);
            return;
        }
        _wakeups.For(evt.Kind).Signal();
        await Task.CompletedTask;
    }
}

/// <summary>
/// SpecStatusChanged → kick the planning-lane schedulers. Each
/// scheduler's TickAsync re-derives candidacy from the store, so all
/// three are kicked on any status change (the kick is free; the tick
/// decides).
/// </summary>
public sealed class SpecStatusChangedConsumer : EventConsumer<SpecStatusChanged>
{
    private readonly SchedulerWakeups _wakeups;

    public SpecStatusChangedConsumer(
        ITransport transport,
        SchedulerWakeups wakeups,
        ILogger<SpecStatusChangedConsumer> logger)
        : base(transport, logger)
    {
        _wakeups = wakeups;
    }

    protected override Task HandleAsync(SpecStatusChanged evt, CancellationToken ct)
    {
        _wakeups.Groom.Signal();
        _wakeups.Design.Signal();
        _wakeups.Artist.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>
/// SprintStatusChanged → kick the assembler (a completed sprint means
/// the next one assembles now, not at the backstop) AND the dispatch
/// loop (a newly ACTIVE sprint makes its tasks claimable — observed in
/// the e2e smoke: enqueue kicked dispatch + assembler simultaneously,
/// dispatch saw "no active sprint" and parked until the backstop).
/// </summary>
public sealed class SprintStatusChangedConsumer : EventConsumer<SprintStatusChanged>
{
    private readonly SchedulerWakeups _wakeups;

    public SprintStatusChangedConsumer(
        ITransport transport,
        SchedulerWakeups wakeups,
        ILogger<SprintStatusChangedConsumer> logger)
        : base(transport, logger)
    {
        _wakeups = wakeups;
    }

    protected override Task HandleAsync(SprintStatusChanged evt, CancellationToken ct)
    {
        _wakeups.Assemble.Signal();
        _wakeups.Dispatch.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>
/// FollowUpFiled → kick the groomer (follow-ups are born ungroomed;
/// the ad-hoc pass grooms or closes them).
/// </summary>
public sealed class FollowUpFiledConsumer : EventConsumer<FollowUpFiled>
{
    private readonly SchedulerWakeups _wakeups;

    public FollowUpFiledConsumer(
        ITransport transport,
        SchedulerWakeups wakeups,
        ILogger<FollowUpFiledConsumer> logger)
        : base(transport, logger)
    {
        _wakeups = wakeups;
    }

    protected override Task HandleAsync(FollowUpFiled evt, CancellationToken ct)
    {
        _wakeups.Groom.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>
/// GroomRequested → kick the groomer (explicit groom request, e.g. an
/// operator re-groom nudge from the dashboard).
/// </summary>
public sealed class GroomRequestedConsumer : EventConsumer<GroomRequested>
{
    private readonly SchedulerWakeups _wakeups;

    public GroomRequestedConsumer(
        ITransport transport,
        SchedulerWakeups wakeups,
        ILogger<GroomRequestedConsumer> logger)
        : base(transport, logger)
    {
        _wakeups = wakeups;
    }

    protected override Task HandleAsync(GroomRequested evt, CancellationToken ct)
    {
        _wakeups.Groom.Signal();
        return Task.CompletedTask;
    }
}
