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
/// </summary>
public sealed class ModelRateLimitTracker
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(3);

    // Provider-wide cooldown key ("provider|*"): account-level quota
    // 429s on providers whose limits are user-level and shared across
    // models (Kimi documents exactly that) cool EVERY model on the
    // provider, not just the one that happened to trip the limit.
    private const string ProviderWideSuffix = "|*";

    private readonly ConcurrentDictionary<string, DateTime> _untilByModel = new(StringComparer.Ordinal);

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

    public void RecordRateLimit(string provider, string model, TimeSpan? cooldown = null)
        => _untilByModel[Key(provider, model)] = DateTime.UtcNow + (cooldown ?? DefaultCooldown);

    /// <summary>Account-level quota exhaustion on a shared-quota
    /// provider: cool every model on the provider.</summary>
    public void RecordProviderRateLimit(string provider, TimeSpan? cooldown = null)
        => _untilByModel[provider + ProviderWideSuffix] = DateTime.UtcNow + (cooldown ?? DefaultCooldown);

    /// <summary>Lift a model-specific cooldown (e.g. the short shed
    /// window an overload-retrying caller records, once its retry
    /// succeeds and the model proves healthy again). Provider-wide
    /// quota cooldowns are NOT touched.</summary>
    public void Clear(string provider, string model)
        => _untilByModel.TryRemove(Key(provider, model), out _);

    /// <summary>Live (provider|model → until) snapshot for diagnostics.</summary>
    public IReadOnlyDictionary<string, DateTime> Snapshot()
        => _untilByModel
            .Where(kv => DateTime.UtcNow < kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
}
