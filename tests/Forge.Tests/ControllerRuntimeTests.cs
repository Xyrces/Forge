using Forge.Core.Control;
using Forge.Orchestrator.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class ControllerRuntimeTests
{
    [Fact]
    public async Task StandbyNeverConstructsWorkOrAcquiresOwnership()
    {
        var store = new Lease();
        var calls = 0;
        await using var runtime = Runtime(store, (_, _, _) => { calls++; return Task.CompletedTask; });
        var status = await runtime.StatusAsync();
        Assert.True(status.Ready);
        Assert.Equal("standby", status.Mode);
        Assert.False(status.AdmissionOpen);
        Assert.Equal(0, status.ActiveRuns);
        Assert.Equal(0, calls);
        Assert.Equal(0, store.Acquisitions);
    }

    [Fact]
    public async Task ActivationIsBoundToOperationAndInstance()
    {
        var store = new Lease();
        var run = new Work();
        await using var runtime = Runtime(store, run.Run);
        Assert.True(await runtime.ActivateAsync("release-one"));
        Assert.True(await runtime.ActivateAsync("release-one"));
        Assert.False(await runtime.ActivateAsync("unrelated-release"));
        Assert.Equal(1, store.Acquisitions);
        Assert.Equal("pod", store.State.OwnerInstanceId);
        Assert.Equal(1, run.Starts);
    }

    [Fact]
    public async Task DrainClosesAdmissionButCountsWorkUntilCleanupCompletes()
    {
        var store = new Lease();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new Work { Cleanup = cleanup.Task };
        var runtime = Runtime(store, run.Run);
        try
        {
            await runtime.ActivateAsync("release");
            await runtime.DrainAsync();
            var status = await runtime.StatusAsync();
            Assert.False(status.AdmissionOpen);
            Assert.Equal("draining", status.Mode);
            Assert.Equal(1, status.ActiveRuns);
            Assert.Equal(0, store.Releases);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EnsureAdmissionAsync(default));
        }
        finally { cleanup.TrySetResult(); await runtime.DisposeAsync(); }
        Assert.Equal(1, store.Releases);
    }

    [Fact]
    public async Task StaleOwnershipClosesAdmissionAndCancelsLocalRun()
    {
        var store = new Lease();
        var run = new Work();
        await using var runtime = Runtime(store, run.Run);
        await runtime.ActivateAsync("release");
        store.State = store.State with { OwnerInstanceId = "replacement", Epoch = 2 };
        Assert.False((await runtime.StatusAsync()).AdmissionOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.EnsureAdmissionAsync(default));
        await run.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task HeartbeatFailureClosesAdmissionAndCancelsWork()
    {
        var store = new Lease { FailRenew = true };
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new Work { Cleanup = cleanup.Task };
        var runtime = Runtime(store, run.Run, TimeSpan.FromMilliseconds(10));
        try
        {
            await runtime.ActivateAsync("release");
            await run.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var status = await runtime.StatusAsync();
            Assert.False(status.AdmissionOpen);
            Assert.False(status.Ready);
            Assert.Equal(1, status.ActiveRuns);
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.EnsureAdmissionAsync(default));
        }
        finally { cleanup.TrySetResult(); await runtime.DisposeAsync(); }
    }

    [Fact]
    public async Task ConflictingOwnerNeverStartsWork()
    {
        var store = new Lease();
        store.State = store.State with { OwnerInstanceId = "other" };
        var run = new Work();
        await using var runtime = Runtime(store, run.Run);
        Assert.False(await runtime.ActivateAsync("release"));
        Assert.Equal(0, run.Starts);
        Assert.True(await runtime.ActivateAsync("release", "other"));
    }

    private static ControllerRuntime Runtime(Lease store,
        Func<Action, Func<CancellationToken, Task>, CancellationToken, Task> run, TimeSpan? heartbeat = null)
        => new(store, "dataset", "pod", run, NullLogger.Instance, heartbeat);

    private sealed class Work
    {
        public int Starts;
        public Task Cleanup = Task.CompletedTask;
        public TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Run(Action ready, Func<CancellationToken, Task> admission, CancellationToken ct)
        {
            Starts++;
            ready();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Cancelled.TrySetResult(); await Cleanup; }
        }
    }

    private sealed class Lease : IControlLeaseStore
    {
        public ControlLeaseState State = new("dataset", null, 0, null, DateTimeOffset.UtcNow, 25);
        public int Acquisitions, Releases;
        public bool FailRenew;
        public Task InitializeDatasetAsync(string datasetId, decimal budgetLimitUsd, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ControlLeaseState?> ReadAsync(string datasetId, CancellationToken ct = default) => Task.FromResult<ControlLeaseState?>(State);
        public Task<ControlLeaseState?> TryAcquireAsync(string datasetId, string instanceId, string operationId, string? expectedPreviousOwner = null, CancellationToken ct = default)
        {
            Acquisitions++;
            if (State.OwnerInstanceId != expectedPreviousOwner) return Task.FromResult<ControlLeaseState?>(null);
            State = State with { OwnerInstanceId = instanceId, Epoch = State.Epoch + 1 };
            return Task.FromResult<ControlLeaseState?>(State);
        }
        public Task<bool> RenewAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default)
            => FailRenew ? Task.FromException<bool>(new IOException("SQL unavailable")) : IsOwnerAsync(datasetId, instanceId, epoch, ct);
        public async Task<bool> ReleaseAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default)
        {
            Releases++;
            if (!await IsOwnerAsync(datasetId, instanceId, epoch, ct)) return false;
            State = State with { OwnerInstanceId = null };
            return true;
        }
        public Task<bool> IsOwnerAsync(string datasetId, string instanceId, long epoch, CancellationToken ct = default)
            => Task.FromResult(State.OwnerInstanceId == instanceId && State.Epoch == epoch);
    }
}
