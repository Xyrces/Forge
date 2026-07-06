using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Designs;

[FeatureState]
public sealed record DesignsState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<DesignRow> Rows { get; init; } = Array.Empty<DesignRow>();
}

public sealed record DesignRow(
    string Id,
    string SpecId,
    string Kind,
    string Title,
    string BodyKind,
    string Status,
    string Author,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public static class DesignsActions
{
    public sealed record LoadDesignsAction(string ProjectId);
    public sealed record DesignsLoadedAction(IReadOnlyList<DesignRow> Rows);
    public sealed record DesignsLoadFailedAction(string Error);
}

public sealed class DesignsClient
{
    private readonly HttpClient _http;
    public DesignsClient(HttpClient http) { _http = http; }
    public async Task<IReadOnlyList<DesignRow>> ListAsync(string projectId, string? status, CancellationToken ct)
    {
        var url = $"/api/designs?projectId={Uri.EscapeDataString(projectId)}";
        if (!string.IsNullOrEmpty(status)) url += "&status=" + Uri.EscapeDataString(status);
        return await _http.GetFromJsonAsync<List<DesignRow>>(url, ct) ?? new List<DesignRow>();
    }
}