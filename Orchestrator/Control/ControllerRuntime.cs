using Forge.Core.Control;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Control;

public sealed record ControllerStatus(string InstanceId, string Mode, bool Ready, bool AdmissionOpen,
    int ActiveRuns, long Epoch, string? LeaseOwner, string DatasetId, int? SchemaVersion = null);

/// <summary>One fenced owner. Standby does not construct any Forge scheduler or store with DDL.</summary>
public sealed class ControllerRuntime : IAsyncDisposable
{
    private readonly IControlLeaseStore _store;
    private readonly string _dataset, _instance;
    private readonly Func<Action, Func<CancellationToken, Task>, CancellationToken, Task> _run;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly SemaphoreSlim _transition = new(1);
    private CancellationTokenSource? _runStop;
    private Task? _runTask;
    private long _epoch;
    private string? _activationOperation;
    private volatile bool _admissionOpen;
    private volatile string _mode = "standby";

    public ControllerRuntime(IControlLeaseStore store, string dataset, string instance,
        Func<Action, Func<CancellationToken, Task>, CancellationToken, Task> run, ILogger logger,
        TimeSpan? heartbeatInterval = null)
    {
        _store = store; _dataset = dataset; _instance = instance; _run = run; _logger = logger;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
    }

    public async Task<ControllerStatus> StatusAsync(CancellationToken ct = default)
    {
        var state = await _store.ReadAsync(_dataset, ct)
            ?? throw new InvalidOperationException("The initialized dataset does not match this deployment.");
        var own = state.OwnerInstanceId == _instance && state.Epoch == Interlocked.Read(ref _epoch);
        return new(_instance, _mode, _mode == "standby" || (_mode == "active" && own && _admissionOpen),
            _admissionOpen && own, _runTask is { IsCompleted: false } && _mode != "standby" ? 1 : 0,
            state.Epoch, state.OwnerInstanceId, _dataset);
    }

    public async Task<bool> ActivateAsync(string operationId, string? expectedPreviousOwner = null,
        CancellationToken ct = default)
    {
        await _transition.WaitAsync(ct);
        try
        {
            if (_runTask is { IsCompleted: false })
                return operationId == _activationOperation && _mode == "active"
                    && await _store.IsOwnerAsync(_dataset, _instance, _epoch, ct);
            var acquired = await _store.TryAcquireAsync(_dataset, _instance, operationId, expectedPreviousOwner, ct);
            if (acquired is null) return false;
            _epoch = acquired.Epoch;
            _activationOperation = operationId;
            _runStop?.Dispose();
            _runStop = new CancellationTokenSource();
            _mode = "activating";
            _runTask = RunOwnedAsync(_runStop.Token);
            return true;
        }
        finally { _transition.Release(); }
    }

    public async Task DrainAsync(CancellationToken ct = default)
    {
        await _transition.WaitAsync(ct);
        try
        {
            _admissionOpen = false;
            if (_runTask is { IsCompleted: false })
            {
                _mode = "draining";
                _runStop!.Cancel();
            }
        }
        finally { _transition.Release(); }
    }

    public async Task EnsureAdmissionAsync(CancellationToken ct)
    {
        if (!_admissionOpen || _mode != "active") throw new OperationCanceledException("Controller admission is closed.", ct);
        if (!await _store.IsOwnerAsync(_dataset, _instance, _epoch, ct))
        {
            _admissionOpen = false;
            _runStop?.Cancel();
            throw new InvalidOperationException("Controller lost its SQL ownership; new effects are denied.");
        }
    }

    private async Task RunOwnedAsync(CancellationToken ct)
    {
        var heartbeat = HeartbeatAsync(ct);
        var failed = false;
        try
        {
            await _run(() =>
            {
                ct.ThrowIfCancellationRequested();
                _mode = "active";
                _admissionOpen = true;
            }, EnsureAdmissionAsync, ct);
            if (!ct.IsCancellationRequested) failed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogError(ex, "Owned Forge runtime failed; admission remains closed.");
        }
        finally
        {
            _admissionOpen = false;
            _runStop!.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _store.ReleaseAsync(_dataset, _instance, _epoch, timeout.Token);
            }
            catch (Exception ex) { _logger.LogError(ex, "Could not release SQL ownership; explicit deployment reconciliation is required."); }
            _mode = failed ? "failed" : "standby";
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (!await _store.RenewAsync(_dataset, _instance, _epoch, timeout.Token))
                    throw new InvalidOperationException("SQL ownership was replaced.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Controller heartbeat failed; cancelling all local work.");
            _admissionOpen = false;
            _runStop!.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAsync();
        if (_runTask is not null) await _runTask;
        _runStop?.Dispose();
        _transition.Dispose();
    }
}
