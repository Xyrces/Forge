using System.Collections.Concurrent;
using Forge.Core;
using Microsoft.Extensions.Logging;

namespace Forge.Agents;

/// <summary>
/// Live provider API-key snapshot. Boot-time resolution
/// (<c>Program.ResolveProviderApiKeysAsync</c>) fills the config once;
/// this resolver re-reads <c>&lt;provider&gt;_api_key</c> secrets on a
/// refresh loop so a rotation via the dashboard Secrets page takes
/// effect WITHOUT a service restart (which would kill in-flight runs).
/// The chat-client factory consults it per <c>Create</c>; the client
/// cache keys on a hash of the key, so a rotated key builds a fresh
/// client and the stale one is simply never used again.
/// </summary>
public sealed class ProviderApiKeyResolver
{
    private readonly ISecretStore _secrets;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _projectIds;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public ProviderApiKeyResolver(
        ISecretStore secrets,
        Func<CancellationToken, Task<IReadOnlyList<string>>> projectIds,
        ILogger logger)
    {
        _secrets = secrets;
        _projectIds = projectIds;
        _logger = logger;
    }

    /// <summary>The latest known plaintext for a provider, or null when
    /// no refresh has found one (the factory then falls back to the
    /// boot-resolved config value).</summary>
    public string? Get(string providerName) =>
        _keys.TryGetValue(providerName, out var k) ? k : null;

    /// <summary>Re-read every registered provider's secret across all
    /// projects. Missing secrets DROP the cached value (operator
    /// deleted the key — fall back to boot config rather than silently
    /// using a revoked credential).</summary>
    public async Task RefreshAsync(IEnumerable<string> providerNames, CancellationToken ct)
    {
        IReadOnlyList<string> projects;
        try
        {
            projects = await _projectIds(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProviderApiKeyResolver: project list read failed; keeping current keys");
            return;
        }

        foreach (var provider in providerNames)
        {
            var kind = SecretKinds.ForProvider(provider);
            string? found = null;
            foreach (var projectId in projects)
            {
                try
                {
                    found = await _secrets.GetPlaintextAsync(projectId, kind, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ProviderApiKeyResolver: {Kind} read failed for {ProjectId}; trying next project", kind, projectId);
                    continue;
                }
                if (!string.IsNullOrEmpty(found)) break;
            }

            if (string.IsNullOrEmpty(found))
            {
                _keys.TryRemove(provider, out _);
                continue;
            }
            if (_keys.TryGetValue(provider, out var current) && current == found) continue;
            _keys[provider] = found;
            _logger.LogInformation("ProviderApiKeyResolver: provider '{Provider}' api key refreshed ({Kind})", provider, kind);
        }
    }
}
