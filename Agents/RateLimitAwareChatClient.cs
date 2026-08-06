using Microsoft.Extensions.AI;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// The single funnel for LLM pressure policy. Wraps every cached
/// provider client so EVERY subsystem that talks to a model —
/// engineering runs, groomer, designer, reviewer sweep, memory
/// extractor, intake — shares three behaviors:
///
/// <para>1. <b>Fail fast during cooldown</b>: if the model is in a
/// tracked 429 cooldown, throw BEFORE any HTTP request leaves the
/// process. The exception message deliberately matches the 429
/// pattern (<c>IsLlmRateLimited</c>) so every existing handler
/// treats it exactly like a provider 429 (requeue, skip tick) —
/// minus the wasted round-trip.</para>
///
/// <para>2. <b>Concurrency permit</b>: a semaphore per provider caps
/// simultaneous round-trips. "Rates for several concurrent agents"
/// (operator, 2026-07-24): 2 coredev slots + groomer + reviewer can
/// otherwise burst 4-5 parallel requests at a free-tier quota.</para>
///
/// <para>3. <b>Record 429s centrally</b>: a 429 from ANY subsystem
/// cools the model for ALL subsystems, honoring Retry-After when
/// the provider sends one (fallback: the tracker's default).</para>
///
/// <para>4. <b>Overload vs quota</b> (Kimi's error catalogue):
/// transient <i>engine overloaded</i> 429s are retried in place with
/// exponential backoff + jitter (bounded; Retry-After honored) before
/// a cooldown is recorded — peak-hour capacity blips must not fail a
/// whole agent run. Account <i>quota</i> 429s are not retried; on a
/// shared-quota provider (limits are user-level across all models,
/// e.g. Kimi) they cool the WHOLE provider, not just one model.</para>
/// </summary>
internal sealed class RateLimitAwareChatClient : DelegatingChatClient
{
    private readonly string _provider;
    private readonly string _model;
    private readonly ModelRateLimitTracker _tracker;
    private readonly SemaphoreSlim _permit;
    private readonly bool _sharedQuota;
    private readonly int _overloadRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Exponential backoff for in-place overload retries. Capped per
    // wait so a Retry-After of "5 minutes" doesn't park the permit
    // for the whole window — that long means a real cooldown.
    private static readonly TimeSpan[] OverloadBackoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan MaxOverloadWait = TimeSpan.FromMinutes(2);

    public RateLimitAwareChatClient(
        IChatClient inner, string provider, string model,
        ModelRateLimitTracker tracker, SemaphoreSlim permit,
        bool sharedQuota = false, int overloadRetries = 3,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(inner)
    {
        _provider = provider;
        _model = model;
        _tracker = tracker;
        _permit = permit;
        _sharedQuota = sharedQuota;
        _overloadRetries = Math.Max(0, overloadRetries);
        _delay = delay ?? Task.Delay;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var coolingUntil = _tracker.CoolingDownUntil(_provider, _model);
        if (coolingUntil is not null)
        {
            throw new InvalidOperationException(
                $"429 Too Many Requests: {_provider}/{_model} is in rate limit cooldown " +
                $"until {coolingUntil.Value:HH:mm:ss} UTC (request suppressed client-side)");
        }

        await _permit.WaitAsync(cancellationToken);
        var held = true;
        try
        {
            var overloadRetry = 0;
            while (true)
            {
                try
                {
                    var response = await base.GetResponseAsync(messages, options, cancellationToken);
                    if (overloadRetry > 0)
                    {
                        // Recovered: lift the shed window early so
                        // queued work resumes immediately. Accepted
                        // race: this can erase a LONGER model cooldown
                        // another caller recorded during our backoff —
                        // the success is genuine health evidence, the
                        // provider-wide quota key is untouched, and a
                        // premature lift self-heals on the next 429.
                        _tracker.Clear(_provider, _model);
                    }
                    return response;
                }
                catch (Exception ex) when (Is429(ex, out var retryAfter, out var kind))
                {
                    if (kind == RateLimitKind.Overloaded && retryAfter is { } longHint && longHint > MaxOverloadWait)
                    {
                        // A hint that long is a real cooldown, not a
                        // blip: cool for the hinted duration and fail
                        // fast instead of retrying early into a
                        // still-limited engine.
                        _tracker.RecordRateLimit(_provider, _model, longHint);
                        throw;
                    }
                    if (kind == RateLimitKind.Overloaded && overloadRetry < _overloadRetries)
                    {
                        // Transient capacity blip: back off and retry
                        // in place. Two anti-starvation measures while
                        // we wait: (1) cool the model for the backoff
                        // window so NEW callers fail fast client-side
                        // instead of queueing behind us (this also
                        // sheds load from the overloaded engine —
                        // Kimi's documented remedy); (2) release the
                        // permit during the sleep so a retrying run
                        // never parks 1 of only 2 provider slots.
                        var wait = OverloadWait(overloadRetry, retryAfter);
                        _tracker.RecordRateLimit(_provider, _model, wait);
                        overloadRetry++;
                        _permit.Release();
                        held = false;
                        try
                        {
                            await _delay(wait, cancellationToken);
                        }
                        finally
                        {
                            await _permit.WaitAsync(cancellationToken);
                            held = true;
                        }
                        continue;
                    }
                    if (kind == RateLimitKind.Quota && _sharedQuota)
                        _tracker.RecordProviderRateLimit(_provider, retryAfter);
                    else
                        _tracker.RecordRateLimit(_provider, _model, retryAfter);
                    throw;
                }
            }
        }
        finally
        {
            if (held) _permit.Release();
        }
    }

    private static TimeSpan OverloadWait(int attempt, TimeSpan? retryAfter)
    {
        var baseWait = OverloadBackoff[Math.Min(attempt, OverloadBackoff.Length - 1)];
        // ±30% jitter so parallel callers don't retry in lockstep.
        var jitter = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.6;
        var wait = TimeSpan.FromTicks((long)(baseWait.Ticks * jitter));
        if (retryAfter is { } hinted && hinted > wait)
            wait = hinted;
        return wait > MaxOverloadWait ? MaxOverloadWait : wait;
    }

    private static bool Is429(Exception ex, out TimeSpan? retryAfter, out RateLimitKind kind)
    {
        retryAfter = null;
        kind = RateLimitKind.Quota;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is LlmRateLimitException rl)
            {
                retryAfter = rl.RetryAfter;
                kind = rl.Kind;
                return true;
            }
            if (e is System.ClientModel.ClientResultException cre && cre.Status == 429)
            {
                retryAfter = ParseRetryAfter(cre);
                kind = LlmRateLimitException.Classify(cre.Message);
                return true;
            }
        }
        var msg = ex.Message;
        if (msg.Contains("429")
            && (msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
        {
            kind = LlmRateLimitException.Classify(msg);
            return true;
        }
        return false;
    }

    private static TimeSpan? ParseRetryAfter(System.ClientModel.ClientResultException ex)
    {
        try
        {
            var raw = ex.GetRawResponse();
            if (raw?.Headers.TryGetValue("Retry-After", out var value) == true
                && int.TryParse(value, out var seconds)
                && seconds > 0 && seconds < 3600)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch { /* header parsing is best-effort */ }
        return null;
    }
}
