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
/// </summary>
internal sealed class RateLimitAwareChatClient : DelegatingChatClient
{
    private readonly string _provider;
    private readonly string _model;
    private readonly ModelRateLimitTracker _tracker;
    private readonly SemaphoreSlim _permit;

    public RateLimitAwareChatClient(
        IChatClient inner, string provider, string model,
        ModelRateLimitTracker tracker, SemaphoreSlim permit)
        : base(inner)
    {
        _provider = provider;
        _model = model;
        _tracker = tracker;
        _permit = permit;
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
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (Is429(ex, out var retryAfter))
        {
            _tracker.RecordRateLimit(_provider, _model, retryAfter);
            throw;
        }
        finally
        {
            _permit.Release();
        }
    }

    private static bool Is429(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.ClientModel.ClientResultException cre && cre.Status == 429)
            {
                retryAfter = ParseRetryAfter(cre);
                return true;
            }
        }
        var msg = ex.Message;
        return msg.Contains("429")
            && (msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
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
