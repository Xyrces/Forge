using System.Collections.Concurrent;

namespace Forge.Orchestrator;

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

    private readonly ConcurrentDictionary<string, DateTime> _untilByModel = new(StringComparer.Ordinal);

    private static string Key(string provider, string model) => provider + "|" + model;

    public bool IsCoolingDown(string provider, string model)
        => _untilByModel.TryGetValue(Key(provider, model), out var until) && DateTime.UtcNow < until;

    /// <summary>Cooldown expiry for a model, or null when claimable.</summary>
    public DateTime? CoolingDownUntil(string provider, string model)
        => _untilByModel.TryGetValue(Key(provider, model), out var until) && DateTime.UtcNow < until
            ? until
            : null;

    public void RecordRateLimit(string provider, string model, TimeSpan? cooldown = null)
        => _untilByModel[Key(provider, model)] = DateTime.UtcNow + (cooldown ?? DefaultCooldown);

    /// <summary>Live (provider|model → until) snapshot for diagnostics.</summary>
    public IReadOnlyDictionary<string, DateTime> Snapshot()
        => _untilByModel
            .Where(kv => DateTime.UtcNow < kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
}
