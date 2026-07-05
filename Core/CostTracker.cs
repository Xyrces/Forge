using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Forge.Core;

/// <summary>
/// Per-call token-usage aggregator. The factory constructs a
/// short-lived <see cref="DelegatingChatClient"/> per session
/// that forwards every <c>UsageDetails</c> into the shared
/// <see cref="CostTracker"/> singleton (this class). The
/// tracker exposes <see cref="Snapshot"/> for the dashboard.
///
/// <para>
/// <b>What we observe:</b> the token counts in the LLM response — i.e.
/// what was sent over the wire after the Headroom proxy's
/// compression. This is the cost the provider bills us for.
/// </para>
///
/// <para>
/// <b>What we don't observe here:</b> the pre-compression size.
/// That's reported by Headroom's own <c>/stats</c> endpoint
/// (see <see cref="HeadroomStatsSnapshot"/>). The dashboard
/// shows both side-by-side when Headroom is enabled.
/// </para>
/// </summary>
public sealed class CostTracker
{
    private readonly ConcurrentQueue<CallRecord> _recent = new();
    private const int RecentLimit = 200;
    private long _totalInput;
    private long _totalOutput;
    private long _callCount;
    private readonly object _lock = new();

    public CostStats Snapshot() => new(
        CallCount: Interlocked.Read(ref _callCount),
        TotalInputTokens: Interlocked.Read(ref _totalInput),
        TotalOutputTokens: Interlocked.Read(ref _totalOutput),
        Recent: _recent.ToArray());

    public void Reset()
    {
        lock (_lock)
        {
            _totalInput = 0;
            _totalOutput = 0;
            _callCount = 0;
            while (_recent.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// Record one call's token counts. Called by the per-session
    /// <see cref="DelegatingChatClient"/> wrappers the factory
    /// constructs around every <see cref="IChatClient"/>.
    /// </summary>
    public void Record(UsageDetails? usage, string? roleHint)
    {
        if (usage is null) return;
        var input = (long)(usage.InputTokenCount ?? 0);
        var output = (long)(usage.OutputTokenCount ?? 0);
        if (input == 0 && output == 0) return;
        lock (_lock)
        {
            _totalInput += input;
            _totalOutput += output;
            _callCount++;
            _recent.Enqueue(new CallRecord(DateTime.UtcNow, input, output, roleHint ?? ""));
            while (_recent.Count > RecentLimit && _recent.TryDequeue(out _)) { }
        }
    }

    public sealed record CallRecord(DateTime At, long InputTokens, long OutputTokens, string Role);
}

public sealed record CostStats(long CallCount, long TotalInputTokens, long TotalOutputTokens, CostTracker.CallRecord[] Recent);

/// <summary>
/// Optional snapshot of Headroom's <c>/stats</c> endpoint, fetched
/// by the dashboard when Headroom is enabled. The orchestrator
/// doesn't fetch this directly — the dashboard does, on a
/// 30s poll. We keep this typed so the dashboard can render
/// the pre-compression vs post-compression delta.
/// </summary>
public sealed record HeadroomStatsSnapshot(
    long TotalInputTokens,
    long TotalOutputTokens,
    long CompressedInputTokens,
    long CompressedOutputTokens,
    long CacheHitCount,
    long CacheMissCount,
    DateTime FetchedAt);

/// <summary>
/// Placeholder <see cref="IChatClient"/> used when
/// <see cref="CostTracker"/> is constructed for the shared
/// aggregator (no inner client needed; the tracker never
/// delegates calls).
/// </summary>
internal sealed class NoOpChatClient : IChatClient
{
    public static readonly NoOpChatClient Instance = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NoOpChatClient is a placeholder for CostTracker; calls should never reach it.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("NoOpChatClient is a placeholder for CostTracker.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}