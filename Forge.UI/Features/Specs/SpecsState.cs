using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Specs;

[FeatureState]
public sealed record SpecsState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SpecRow> Rows { get; init; } = Array.Empty<SpecRow>();
    public string StatusFilter { get; init; } = "All";
}

public sealed record SpecRow(
    string Id,
    string ProjectId,
    string Title,
    string Status,
    int CurrentVersion,
    string? ParentIssueId,
    string? ParentSpecId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public static class SpecsActions
{
    public sealed record LoadSpecsAction(string? ProjectId = null);
    public sealed record SpecsLoadedAction(IReadOnlyList<SpecRow> Rows);
    public sealed record SpecsLoadFailedAction(string Error);

    public sealed record SetStatusFilterAction(string Filter, string? ProjectId = null);
}

public sealed class SpecsClient
{
    private readonly HttpClient _http;

    public SpecsClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<SpecRow>> ListAsync(string? status, string? projectId, CancellationToken ct)
    {
        var qs = new List<string>(2);
        if (!string.IsNullOrEmpty(status) && status != "All")
            qs.Add("status=" + Uri.EscapeDataString(status));
        if (!string.IsNullOrEmpty(projectId))
            qs.Add("project=" + Uri.EscapeDataString(projectId));
        var url = qs.Count == 0 ? "/api/specs" : "/api/specs?" + string.Join('&', qs);
        return await _http.GetFromJsonAsync<List<SpecRow>>(url, ct) ?? new List<SpecRow>();
    }

    public async Task<ActionsDto> GetActionsAsync(string id, CancellationToken ct)
        => await _http.GetFromJsonAsync<ActionsDto>($"/api/specs/{Uri.EscapeDataString(id)}/actions", ct) ?? new ActionsDto(false, false, false, null);
}

public sealed record ActionsDto(bool CanApprove, bool CanStartGrooming, bool CanShip, string? Reason);