using System.Collections.Concurrent;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Operator-editable per-role model assignments, persisted in the
/// primary project's MemoryStore. Keys are PROJECT-SCOPED
/// (<c>llm/roleModel/&lt;projectId&gt;/&lt;AgentType&gt;</c>) with a
/// global fallback (<c>llm/roleModel/&lt;AgentType&gt;</c>) — an
/// override set for one project must NEVER leak into another
/// project's runs (operator rule 2026-07-30). Resolution order for a
/// run: project override → global override → appsettings
/// <c>llm.roles</c> → provider default.
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
    private const string Prefix = "llm/roleModel/";

    private readonly MemoryStore _memory;
    // Cache key: "<projectId>|<AgentType>" ("" projectId = global).
    private readonly ConcurrentDictionary<string, RoleModel> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RoleModelOverrides(MemoryStore memory) => _memory = memory;

    private static string Key(AgentType role, string? projectId)
        => projectId is null ? $"{Prefix}{role}" : $"{Prefix}{projectId}/{role}";

    private static string CacheKey(AgentType role, string? projectId)
        => (projectId ?? "") + "|" + role;

    /// <summary>Synchronous snapshot read for the run hot path: the
    /// project-scoped override wins; the global override is the
    /// fallback. Null projectId = global only.</summary>
    public RoleModel? Get(AgentType role, string? projectId = null)
    {
        if (projectId is not null
            && _cache.TryGetValue(CacheKey(role, projectId), out var scoped))
        {
            return scoped;
        }
        return _cache.TryGetValue(CacheKey(role, null), out var global) ? global : null;
    }

    /// <summary>Which override applies for (role, projectId): the
    /// project-scoped one, the global one, or none.</summary>
    public string? GetScope(AgentType role, string? projectId)
    {
        if (projectId is not null && _cache.ContainsKey(CacheKey(role, projectId)))
            return "project";
        if (_cache.ContainsKey(CacheKey(role, null)))
            return "global";
        return null;
    }

    /// <summary>Rehydrate the snapshot from the store (startup).
    /// Key shape after the prefix: one segment = global, two =
    /// project-scoped.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var row in await _memory.RecallAsync(Prefix, ct))
        {
            var rest = row.Key[Prefix.Length..];
            var parsed = Parse(row.Body);
            if (parsed is null) continue;
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                if (Enum.TryParse<AgentType>(rest, out var role))
                    _cache[CacheKey(role, null)] = parsed;
            }
            else if (Enum.TryParse<AgentType>(rest[(slash + 1)..], out var role))
            {
                _cache[CacheKey(role, rest[..slash])] = parsed;
            }
        }
    }

    public async Task SetAsync(AgentType role, string provider, string model, CancellationToken ct = default, string? projectId = null)
    {
        await _memory.RememberAsync(Key(role, projectId), $"{provider}|{model}", ttlDays: null, ct);
        _cache[CacheKey(role, projectId)] = new RoleModel(provider, model);
    }

    public async Task ClearAsync(AgentType role, CancellationToken ct = default, string? projectId = null)
    {
        await _memory.ForgetAsync(Key(role, projectId), ct);
        _cache.TryRemove(CacheKey(role, projectId), out _);
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
    /// project-scoped DB override → global DB override (each only when
    /// it still names a configured provider) →
    /// <see cref="LlmConfig.Resolve"/> (llm.roles → default).
    /// </summary>
    public static (ProviderConfig Provider, string Model, bool IsOverride) ResolveEffective(
        this LlmConfig config, AgentType role, RoleModelOverrides? overrides, string? projectId = null)
    {
        var o = overrides?.Get(role, projectId);
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
