namespace Forge.Agents;

/// <summary>
/// Why a provider returned 429. Kimi's error catalogue
/// (platform.kimi.ai/docs/api/errors) splits the first two:
/// <see cref="Overloaded"/> = server-side capacity ("engine is
/// currently overloaded") — transient, ride it out with backoff;
/// upgrading the account does NOT help. <see cref="Quota"/> =
/// model-level burst limits (RPM/TPM) — cool down briefly.
/// <see cref="AccountQuota"/> = the ACCOUNT-level budget is
/// throttled (MiniMax Token Plan codes 2056/2062). The message is
/// the same whether the 5h/weekly window is exhausted OR dynamic
/// peak-traffic throttling is active (observed live 2026-08-08:
/// 2062 firing with the 5h window at 1% used — MiniMax's FAQ:
/// short-term restriction typically clears in about a minute,
/// limits tighten during peak traffic). Account budgets are shared
/// by every model and slot on the key, so the cooldown must be
/// provider-wide and JITTERED — a flat cooldown makes all slots
/// resume in lockstep and re-trip the throttle (the thundering
/// herd behind the 2026-08-08 34x-2062 day).
/// </summary>
public enum RateLimitKind
{
    Quota,
    Overloaded,
    AccountQuota,
}

/// <summary>
/// A typed 429 from a chat provider. Carries the facts the
/// generic <see cref="HttpRequestException"/> loses: the
/// provider's <c>Retry-After</c> hint, whether the limit is
/// burst quota / transient overload / window quota, the
/// provider's own error code, and its request id (support
/// correlation). The message keeps the "429 ... rate limit"
/// phrasing so the existing <c>IsLlmRateLimited</c> string
/// classification still matches.
/// </summary>
public sealed class LlmRateLimitException : HttpRequestException
{
    public LlmRateLimitException(string message, TimeSpan? retryAfter, RateLimitKind kind,
        string? errorCode = null, string? requestId = null)
        : base(message)
    {
        RetryAfter = retryAfter;
        Kind = kind;
        ErrorCode = errorCode;
        RequestId = requestId;
    }

    /// <summary>The provider's Retry-After hint, when sent.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Burst quota vs transient overload vs window quota.</summary>
    public RateLimitKind Kind { get; }

    /// <summary>The provider's application-level error code (e.g.
    /// MiniMax <c>base_resp.status_code</c> or the trailing "(2062)"
    /// in its Anthropic-shaped error message), when parsed.</summary>
    public string? ErrorCode { get; }

    /// <summary>The provider's request id, when present — the
    /// correlation handle for a support ticket.</summary>
    public string? RequestId { get; }

    /// <summary>
    /// MiniMax codes that mean the ACCOUNT-level Token Plan budget
    /// is throttled (window exhaustion OR dynamic peak control —
    /// the body does not distinguish), per
    /// platform.minimax.io/docs/api-reference/errorcode + the 2062
    /// observed live 2026-08-08 ("Token Plan rate limit reached").
    /// </summary>
    private static readonly HashSet<string> AccountQuotaCodes = new(StringComparer.Ordinal) { "2056", "2062" };

    /// <summary>Classification: overload signature first (Kimi),
    /// then account-quota signals (MiniMax Token Plan), else burst
    /// quota.</summary>
    public static RateLimitKind Classify(string? errorDetail)
    {
        if (errorDetail is null) return RateLimitKind.Quota;
        if (errorDetail.Contains("overload", StringComparison.OrdinalIgnoreCase))
            return RateLimitKind.Overloaded;
        var code = ExtractErrorCode(errorDetail);
        if (code is not null && AccountQuotaCodes.Contains(code))
            return RateLimitKind.AccountQuota;
        if (errorDetail.Contains("Token Plan", StringComparison.OrdinalIgnoreCase)
            || errorDetail.Contains("usage limit exceeded", StringComparison.OrdinalIgnoreCase))
            return RateLimitKind.AccountQuota;
        return RateLimitKind.Quota;
    }

    /// <summary>Pull the provider's numeric error code out of an
    /// error payload: MiniMax's Anthropic-shaped body carries it as a
    /// trailing "(2062)" in the message; the OpenAI-shaped body has
    /// <c>base_resp.status_code</c>. Returns null when absent.</summary>
    public static string? ExtractErrorCode(string? errorDetail)
    {
        if (string.IsNullOrEmpty(errorDetail)) return null;
        // Trailing "(2062)" pattern in the message text.
        var close = errorDetail.LastIndexOf(')');
        if (close > 0)
        {
            var open = errorDetail.LastIndexOf('(', close);
            if (open >= 0)
            {
                var candidate = errorDetail.AsSpan(open + 1, close - open - 1).Trim();
                if (candidate.Length == 4 && int.TryParse(candidate, out _))
                    return candidate.ToString();
            }
        }
        // base_resp.status_code in a JSON body fragment.
        const string marker = "\"status_code\":";
        var idx = errorDetail.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var span = errorDetail.AsSpan(idx + marker.Length).TrimStart();
            var len = 0;
            while (len < span.Length && char.IsDigit(span[len])) len++;
            if (len > 0) return span[..len].ToString();
        }
        return null;
    }

    /// <summary>Pull <c>"request_id":"…"</c> out of an error body
    /// fragment, when present.</summary>
    public static string? ExtractRequestId(string? errorDetail)
    {
        if (string.IsNullOrEmpty(errorDetail)) return null;
        const string marker = "\"request_id\":\"";
        var idx = errorDetail.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end = errorDetail.IndexOf('"', start);
        return end > start ? errorDetail[start..end] : null;
    }
}
