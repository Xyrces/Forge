using System.Collections.Concurrent;

namespace Forge.Orchestrator.Slots;

/// <summary>
/// In-process concurrency slot table keyed by (projectId, role). Each
/// (project, role) pair gets a fixed-size semaphore derived from the
/// project's role caps in <see cref="Configuration.ProjectOptions"/>.
/// Slots are positional (not per-task): a slot is a thread of
/// concurrency, not a process or session. Callers acquire before
/// dispatching a unit of work and release when the dispatch completes
/// (typically via <c>await using</c>).
/// </summary>
public sealed class SlotTable
{
    private readonly ConcurrentDictionary<SlotKey, SemaphoreSlim> _slots = new();
    private readonly ConcurrentDictionary<SlotKey, int> _caps = new();
    private readonly ConcurrentDictionary<SlotKey, int> _inFlight = new();
    private long _totalAcquired;
    private long _totalReleased;

    public int InFlight(string projectId, string role)
        => _inFlight.TryGetValue(new SlotKey(projectId, role), out var n) ? n : 0;

    public int MaxFor(string projectId, string role)
        => _caps.TryGetValue(new SlotKey(projectId, role), out var n) ? n : 0;

    public IReadOnlyList<SlotMeter> Snapshot()
        => _slots.Keys
            .Select(k => new SlotMeter(k.ProjectId, k.Role, InFlight(k.ProjectId, k.Role), MaxFor(k.ProjectId, k.Role)))
            .OrderBy(m => m.ProjectId).ThenBy(m => m.Role)
            .ToList();

    public long TotalAcquired => Interlocked.Read(ref _totalAcquired);
    public long TotalReleased => Interlocked.Read(ref _totalReleased);

    /// <summary>
    /// Register (or re-size) the slot pool for a project+role. Decreasing
    /// the cap below current in-flight is allowed; excess in-flight
    /// holders will continue to run and the new cap is enforced
    /// against new acquires only.
    /// </summary>
    public void Configure(string projectId, string role, int max)
    {
        if (max < 1) throw new ArgumentOutOfRangeException(nameof(max), "slot cap must be >= 1");
        var key = new SlotKey(projectId, role);
        _caps[key] = max;
        _slots.AddOrUpdate(key,
            _ => new SemaphoreSlim(max, max),
            (_, existing) =>
            {
                if (existing.CurrentCount == max) return existing;
                var fresh = new SemaphoreSlim(max, max);
                try { existing.Dispose(); } catch { /* tolerate */ }
                return fresh;
            });
    }

    /// <summary>
    /// Try to claim a slot. Returns <c>null</c> when <paramref name="timeout"/>
    /// elapses without a slot becoming available. The returned handle
    /// releases the slot on dispose — call sites should use
    /// <c>await using</c> with the result of <c>AcquireAsync</c>.
    /// </summary>
    public async Task<SlotHandle?> TryAcquireAsync(
        string projectId, string role, TimeSpan timeout, CancellationToken ct)
    {
        if (!_slots.TryGetValue(new SlotKey(projectId, role), out var sem))
            throw new InvalidOperationException(
                $"Slot not configured for project='{projectId}' role='{role}'. Call Configure first.");
        try
        {
            if (!await sem.WaitAsync(timeout, ct))
                return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        Interlocked.Increment(ref _totalAcquired);
        var key = new SlotKey(projectId, role);
        var n = _inFlight.AddOrUpdate(key, _ => 1, (_, v) => v + 1);
        return new SlotHandle(this, key, n);
    }

    private void Release(SlotKey key)
    {
        if (_slots.TryGetValue(key, out var sem))
            sem.Release();
        Interlocked.Increment(ref _totalReleased);
        _inFlight.AddOrUpdate(key, _ => 0, (_, v) => Math.Max(0, v - 1));
    }

    public readonly record struct SlotMeter(string ProjectId, string Role, int InFlight, int Max);

    internal readonly record struct SlotKey(string ProjectId, string Role);

    public sealed class SlotHandle : IAsyncDisposable
    {
        private readonly SlotTable _owner;
        private readonly SlotKey _key;
        private readonly int _position;
        private int _disposed;
        internal SlotHandle(SlotTable owner, SlotKey key, int position)
        {
            _owner = owner;
            _key = key;
            _position = position;
        }
        public int Position => _position;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Release(_key);
            await ValueTask.CompletedTask;
        }
    }
}
