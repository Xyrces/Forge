namespace Forge.Agents;

/// <summary>
/// Why a provider returned 429. Kimi's error catalogue
/// (platform.kimi.ai/docs/api/errors) splits these:
/// <see cref="Overloaded"/> = server-side capacity ("engine is
/// currently overloaded") — transient, ride it out with backoff;
/// upgrading the account does NOT help. <see cref="Quota"/> =
/// account-level concurrency/RPM/TPM/TPD exhaustion — cool down
/// for the indicated window.
/// </summary>
public enum RateLimitKind
{
    Quota,
    Overloaded,
}

/// <summary>
/// A typed 429 from a chat provider. Carries the two facts the
/// generic <see cref="HttpRequestException"/> loses: the
/// provider's <c>Retry-After</c> hint and whether the limit is
/// account quota or transient engine overload. The message keeps
/// the "429 ... rate limit" phrasing so the existing
/// <c>IsLlmRateLimited</c> string classification still matches.
/// </summary>
public sealed class LlmRateLimitException : HttpRequestException
{
    public LlmRateLimitException(string message, TimeSpan? retryAfter, RateLimitKind kind)
        : base(message)
    {
        RetryAfter = retryAfter;
        Kind = kind;
    }

    /// <summary>The provider's Retry-After hint, when sent.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Quota exhaustion vs transient engine overload.</summary>
    public RateLimitKind Kind { get; }

    /// <summary>Kimi's overload signature: the error body says the
    /// engine/node is overloaded. Anything else on a 429 is treated
    /// as account quota.</summary>
    public static RateLimitKind Classify(string? errorDetail) =>
        errorDetail?.Contains("overload", StringComparison.OrdinalIgnoreCase) == true
            ? RateLimitKind.Overloaded
            : RateLimitKind.Quota;
}
