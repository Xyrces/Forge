using System.Net.Http.Headers;
using System.Text.Json;

namespace Forge.Agents;

public sealed record ModelCatalogProbeResult(bool Success, string Detail);

/// <summary>Read-only model metadata probe. Never calls a generation endpoint or falls back to one.</summary>
public static class ModelCatalogProbe
{
    public static async Task<ModelCatalogProbeResult> CheckAsync(HttpClient http, ProviderConfig provider,
        string model, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(provider.Api)
            && !string.Equals(provider.Api, "responses", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider.Api, "openai", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Provider '{provider.Name}' protocol '{provider.Api}' has no supported non-generation model-access probe.");
        if (string.IsNullOrWhiteSpace(provider.ApiKey)) return new(false, $"Provider '{provider.Name}' has no API key for model metadata access.");
        if (string.IsNullOrWhiteSpace(model)) return new(false, $"Provider '{provider.Name}' has no configured model.");
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelUrl(provider.BaseUrl, model));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        if (!string.IsNullOrWhiteSpace(provider.OrgId)) request.Headers.Add("OpenAI-Organization", provider.OrgId);
        if (!string.IsNullOrWhiteSpace(provider.ProjectId)) request.Headers.Add("OpenAI-Project", provider.ProjectId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return new(false, $"Model metadata access for {provider.Name}/{model} returned HTTP {(int)response.StatusCode}; no generation was attempted.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("id", out var id) || id.GetString() != model)
            return new(false, $"Model metadata response did not identify configured model {provider.Name}/{model}.");
        return new(true, $"Model metadata accessible for {provider.Name}/{model}; generation capability and quota were not tested.");
    }

    internal static Uri ModelUrl(string baseUrl, string model)
    {
        var root = new Uri(baseUrl, UriKind.Absolute);
        if (root.Scheme is not ("http" or "https") || root.Query.Length != 0 || root.Fragment.Length != 0 || root.UserInfo.Length != 0)
            throw new ArgumentException("Model API base must be an HTTP(S) URL without query, fragment, or credentials.", nameof(baseUrl));
        var prefix = root.AbsoluteUri.TrimEnd('/');
        if (root.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) && root.AbsolutePath == "/") prefix += "/v1";
        return new Uri(prefix + "/models/" + Uri.EscapeDataString(model));
    }
}
