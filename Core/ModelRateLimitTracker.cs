using System.Collections.Concurrent;

namespace Forge.Core;

/// <summary>
/// Per-(provider, model) LLM 429 cooldowns. Rate-limit quotas live at
/// the provider/model boundary: a 429 from one model (e.g. minimax-m3
/// on the kilo gateway) says nothing about another model's quota
/// (e.g. kimi-k3 reserved for grooming/review). The engineering
/// dispatch loop consults this before claiming — only tasks whose
/// resolved model is cooling down are skipped; tasks on other models
/// proceed immediately. In-memory: a restart forgets the cooldown,
/// which fails safe (at most one extra attempt per model).
///
/// <para>Account-level budgets (MiniMax Token Plan, Kimi user-level
/// quota) are shared by every model AND every concurrent slot on the
/// key, so their cooldown is provider-wide and ESCALATING with
/// jitter: a flat cooldown makes all slots resume in lockstep and
/// re-trip the throttle on the spot (observed live 2026-08-08: four
/// coredev slots 429ing within 700ms of each cooldown expiry, 34
/// Token-Plan 2062s in a day). Escalation resets on the first
/// successful call.</para>
/// </summary>
public sealed class ModelRateLimitTracker
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(3);

    /// <summary>Account-quota escalation ladder (doubling, capped).
    /// Starts at 1 minute: MiniMax's Token Plan FAQ says short-term
    /// throttling typically clears in about a minute; repeat trips
    /// mean the account is still saturated, so back off harder.</summary>
    public static readonly TimeSpan AccountQuotaInitialCooldown = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AccountQuotaMaxCooldown = TimeSpan.FromMinutes(30);

    // Provider-wide cooldown key ("provider|*"): account-level quota
    // 429s on providers whose limits are user-level and shared across
    // models (Kimi documents exactly that; MiniMax Token Plan budgets
    // are account-level) cool EVERY model on the provider, not just
    // the one that happened to trip the limit.
    private const string ProviderWideSuffix = "|*";

    private readonly ConcurrentDictionary<string, DateTime> _untilByModel = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _accountStrikes = new(StringComparer.Ordinal);

    private static string Key(string provider, string model) => provider + "|" + model;

    public bool IsCoolingDown(string provider, string model)
        => CoolingDownUntil(provider, model) is not null;

    /// <summary>Cooldown expiry for a model, or null when claimable.
    /// The later of the model-specific and the provider-wide quota
    /// cooldown wins.</summary>
    public DateTime? CoolingDownUntil(string provider, string model)
    {
        var now = DateTime.UtcNow;
        DateTime? until = null;
        if (_untilByModel.TryGetValue(Key(provider, model), out var modelUntil) && now < modelUntil)
            until = modelUntil;
        if (_untilByModel.TryGetValue(provider + ProviderWideSuffix, out var providerUntil) && now < providerUntil)
            until = until is null || providerUntil > until.Value ? providerUntil : until;
        return until;
    }

    /// <summary>Provider-wide (account-quota) cooldown expiry, or
    /// null. Used to label the suppression message correctly.</summary>
    public DateTime? ProviderCoolingDownUntil(string provider)
        => _untilByModel.TryGetValue(provider + ProviderWideSuffix, out var until)
            && DateTime.UtcNow < until
                ? until
                : null;

    /// <summary>Record a model-scoped cooldown. Never SHORTENS an
    /// active longer cooldown on the same key — a generic 3-minute
    /// handler must not flatten an escalating account-quota backoff
    /// the client layer just recorded (the 2026-08-08 herd bug).</summary>
    public void RecordRateLimit(string provider, string model, TimeSpan? cooldown = null)
    {
        var key = Key(provider, model);
        var expiry = DateTime.UtcNow + (cooldown ?? DefaultCooldown);
        _untilByModel.AddOrUpdate(key, expiry,
            (_, existing) => existing > expiry ? existing : expiry);
    }

    /// <summary>Account-level quota exhaustion on a shared-quota
    /// provider: cool every model on the provider.</summary>
    public void RecordProviderRateLimit(string provider, TimeSpan? cooldown = null)
    {
        var key = provider + ProviderWideSuffix;
        var expiry = DateTime.UtcNow + (cooldown ?? DefaultCooldown);
        _untilByModel.AddOrUpdate(key, expiry,
            (_, existing) => existing > expiry ? existing : expiry);
    }

    /// <summary>
    /// Account-level throttle (MiniMax Token Plan 2056/2062):
    /// provider-wide cooldown that DOUBLES on each consecutive trip
    /// (1m → 2m → 4m → … capped at 30m) with ±30% jitter so the slots
    /// sharing the key resume spread out instead of in lockstep.
    /// Returns the expiry recorded (for logging).
    /// </summary>
    public DateTime RecordAccountQuota(string provider, TimeSpan? retryAfter = null)
    {
        var strike = _accountStrikes.AddOrUpdate(provider, 1, (_, n) => n + 1);
        var span = retryAfter
            ?? TimeSpan.FromTicks(Math.Min(
                AccountQuotaInitialCooldown.Ticks << Math.Min(strike - 1, 10),
                AccountQuotaMaxCooldown.Ticks));
        var jitter = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.6;
        var expiry = DateTime.UtcNow + TimeSpan.FromTicks((long)(span.Ticks * jitter));
        var key = provider + ProviderWideSuffix;
        _untilByModel.AddOrUpdate(key, expiry,
            (_, existing) => existing > expiry ? existing : expiry);
        return expiry;
    }

    /// <summary>Current consecutive account-quota trips (diagnostics).</summary>
    public int AccountStrikes(string provider)
        => _accountStrikes.TryGetValue(provider, out var n) ? n : 0;

    /// <summary>Lift a model-specific cooldown (e.g. the short shed
    /// window an overload-retrying caller records, once its retry
    /// succeeds and the model proves healthy again). A success also
    /// resets the provider's account-quota escalation ladder — the
    /// account demonstrably has budget again. The provider-wide
    /// cooldown entry itself is left to expire (cheap, self-heals).</summary>
    public void Clear(string provider, string model)
    {
        _untilByModel.TryRemove(Key(provider, model), out _);
        _accountStrikes.TryRemove(provider, out _);
    }

    /// <summary>Live (provider|model → until) snapshot for diagnostics.</summary>
    public IReadOnlyDictionary<string, DateTime> Snapshot()
        => _untilByModel
            .Where(kv => DateTime.UtcNow < kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
}
