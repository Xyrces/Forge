using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Projects;

[FeatureState]
public sealed record ProjectsState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ProjectsEndpointRow> Projects { get; init; } = Array.Empty<ProjectsEndpointRow>();
    public DateTime? LastFetchedAt { get; init; }
}

public sealed record ProjectsEndpointRow(
    string Id,
    string Name,
    string Root,
    Dictionary<string, int> Roles,
    int Pending,
    int InProgress,
    int Completed,
    int Failed,
    IReadOnlyList<ProjectsSlotRow> Slots);

public sealed record ProjectsSlotRow(string ProjectId, string Role, int InFlight, int Max);

public static class ProjectsActions
{
    public sealed record LoadProjectsAction();
    public sealed record ProjectsLoadedAction(IReadOnlyList<ProjectsEndpointRow> Projects);
    public sealed record ProjectsLoadFailedAction(string Error);
}

public sealed class ProjectsClient
{
    private readonly HttpClient _http;
    public ProjectsClient(HttpClient http) { _http = http; }

    public async Task<IReadOnlyList<ProjectsEndpointRow>> ListAsync(CancellationToken ct)
    {
        var rows = await _http.GetFromJsonAsync<List<ProjectsEndpointRow>>("/api/projects/", ct);
        return rows ?? new List<ProjectsEndpointRow>();
    }
}
