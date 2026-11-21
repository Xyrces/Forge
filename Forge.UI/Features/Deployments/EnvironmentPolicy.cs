using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Dashboard.Features.Deployments;

public sealed record EnvironmentPolicy(int Revision, string? SprintEnvironment, List<EnvironmentTarget> Environments);
public sealed record EnvironmentTarget(string Name, string DisplayName, string KubernetesNamespace,
    string KubernetesDatasetId, List<string> PromoteTo, bool RequireApproval);

public static class EnvironmentPolicyClient
{
    public static async Task<EnvironmentPolicy> LoadAsync(HttpClient http, string project, CancellationToken ct) =>
        await http.GetFromJsonAsync<EnvironmentPolicy>($"/api/projects/{Uri.EscapeDataString(project)}/environments", ct)
        ?? throw new InvalidDataException("The environment configuration was empty.");

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(text);
            foreach (var key in new[] { "message", "error" })
                if (json.RootElement.TryGetProperty(key, out var message) && message.ValueKind == JsonValueKind.String)
                    throw new InvalidOperationException(message.GetString());
        }
        catch (JsonException) { /* A proxy may return a non-JSON error page. */ }
        throw new HttpRequestException($"The server could not confirm this change ({(int)response.StatusCode}). Refresh before retrying.");
    }
}
