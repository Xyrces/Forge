using System.Collections.Concurrent;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Operator-editable per-role model assignments, persisted in the
/// primary project's MemoryStore (<c>llm/roleModel/&lt;AgentType&gt;</c>
/// = "provider|model"). Resolution order for a run:
/// DB override → appsettings <c>llm.roles</c> → provider default.
///
/// <para>
/// Written live from the dashboard Agents page — no restart: writes
/// update the in-memory snapshot synchronously, and reads (the chat
/// client factory + the run registry's model label) are sync lookups
/// against that snapshot. Single-orchestrator process means the cache
/// is always fresh; <see cref="LoadAsync"/> rehydrates on startup.
/// </para>
/// </summary>
public sealed class RoleModelOverrides
{
    private readonly MemoryStore _memory;
    private readonly ConcurrentDictionary<AgentType, RoleModel> _cache = new();

    public RoleModelOverrides(MemoryStore memory) => _memory = memory;

    private static string Key(AgentType role) => $"llm/roleModel/{role}";

    /// <summary>Synchronous snapshot read for the run hot path.</summary>
    public RoleModel? Get(AgentType role)
        => _cache.TryGetValue(role, out var m) ? m : null;

    /// <summary>Rehydrate the snapshot from the store (startup).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var role in Enum.GetValues<AgentType>())
        {
            var body = (await _memory.RecallAsync(Key(role), ct)).FirstOrDefault()?.Body;
            var parsed = Parse(body);
            if (parsed is not null) _cache[role] = parsed;
        }
    }

    public async Task SetAsync(AgentType role, string provider, string model, CancellationToken ct = default)
    {
        await _memory.RememberAsync(Key(role), $"{provider}|{model}", ttlDays: null, ct);
        _cache[role] = new RoleModel(provider, model);
    }

    public async Task ClearAsync(AgentType role, CancellationToken ct = default)
    {
        await _memory.ForgetAsync(Key(role), ct);
        _cache.TryRemove(role, out _);
    }

    private static RoleModel? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var parts = body.Split('|', 2);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? new RoleModel(parts[0], parts[1])
            : null;
    }
}

public static class LlmConfigOverrideResolution
{
    /// <summary>
    /// Resolve the effective (provider, model) for a role:
    /// DB override first (when it still names a configured provider),
    /// then <see cref="LlmConfig.Resolve"/> (llm.roles → default).
    /// </summary>
    public static (ProviderConfig Provider, string Model, bool IsOverride) ResolveEffective(
        this LlmConfig config, AgentType role, RoleModelOverrides? overrides)
    {
        var o = overrides?.Get(role);
        if (o is not null)
        {
            var provider = config.Providers.FirstOrDefault(p =>
                string.Equals(p.Name, o.ProviderName, StringComparison.OrdinalIgnoreCase));
            if (provider is not null) return (provider, o.Model, true);
            // Dangling override (provider removed from config) — fall
            // through to the configured resolution rather than failing.
        }
        var (p, m) = config.Resolve(role);
        return (p, m, false);
    }
}
