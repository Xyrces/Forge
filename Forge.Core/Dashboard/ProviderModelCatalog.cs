using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Agents;

namespace Forge.Dashboard;

/// <summary>
/// Server-side catalog of a provider's available models, fetched from
/// its OpenAI-compatible <c>GET {baseUrl}/models</c> and cached for 60s
/// (the Agents page model editor turns the free-text input into a
/// searchable dropdown of what's ACTUALLY available). The provider's
/// API key is used server-side only — it never reaches the client.
/// Failures (provider down, non-200, bad JSON) degrade to an empty
/// list so the editor falls back to free text instead of breaking.
/// </summary>
public static class ProviderModelCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, (DateTime FetchedAt, string[] Models)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string[]> GetModelsAsync(ProviderConfig provider, CancellationToken ct)
    {
        if (Cache.TryGetValue(provider.Name, out var hit) && DateTime.UtcNow - hit.FetchedAt < CacheTtl)
            return hit.Models;
        var models = await FetchModelsAsync(provider, SharedHttp, ct);
        Cache[provider.Name] = (DateTime.UtcNow, models);
        return models;
    }

    internal static async Task<string[]> FetchModelsAsync(ProviderConfig provider, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, provider.BaseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrEmpty(provider.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            return ParseModelIds(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>OpenAI shape: <c>{ "data": [ { "id": "provider/model", ... } ] }</c>.</summary>
    internal static string[] ParseModelIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return data.EnumerateArray()
                .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
