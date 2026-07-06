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
    public sealed record LoadSpecsAction();
    public sealed record SpecsLoadedAction(IReadOnlyList<SpecRow> Rows);
    public sealed record SpecsLoadFailedAction(string Error);

    public sealed record SetStatusFilterAction(string Filter);
}

public sealed class SpecsClient
{
    private readonly HttpClient _http;

    public SpecsClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<SpecRow>> ListAsync(string? status, CancellationToken ct)
    {
        var url = string.IsNullOrEmpty(status) || status == "All"
            ? "/api/specs"
            : "/api/specs?status=" + Uri.EscapeDataString(status);
        return await _http.GetFromJsonAsync<List<SpecRow>>(url, ct) ?? new List<SpecRow>();
    }

    public async Task<ActionsDto> GetActionsAsync(string id, CancellationToken ct)
        => await _http.GetFromJsonAsync<ActionsDto>($"/api/specs/{Uri.EscapeDataString(id)}/actions", ct) ?? new ActionsDto(false, false, false, null);
}

public sealed record ActionsDto(bool CanApprove, bool CanStartGrooming, bool CanShip, string? Reason);