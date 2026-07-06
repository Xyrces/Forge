using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Art;

[FeatureState]
public sealed record ArtState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ArtRow> Rows { get; init; } = Array.Empty<ArtRow>();
}

public sealed record ArtRow(
    string Id,
    string SpecId,
    string Kind,
    string Title,
    string BodyKind,
    string Status,
    string Author,
    string FileUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public static class ArtActions
{
    public sealed record LoadArtAction(string ProjectId);
    public sealed record ArtLoadedAction(IReadOnlyList<ArtRow> Rows);
    public sealed record ArtLoadFailedAction(string Error);
}

public sealed class ArtClient
{
    private readonly HttpClient _http;
    public ArtClient(HttpClient http) { _http = http; }
    public async Task<IReadOnlyList<ArtRow>> ListAsync(string projectId, string? status, CancellationToken ct)
    {
        var url = $"/api/art-output?projectId={Uri.EscapeDataString(projectId)}";
        if (!string.IsNullOrEmpty(status)) url += "&status=" + Uri.EscapeDataString(status);
        return await _http.GetFromJsonAsync<List<ArtRow>>(url, ct) ?? new List<ArtRow>();
    }
}