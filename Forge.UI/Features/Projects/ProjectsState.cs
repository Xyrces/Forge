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
    public bool Submitting { get; init; }
    public string? AddError { get; init; }
    public string? LastAdded { get; init; }
}

public sealed record ProjectsEndpointRow(
    string Id,
    string Name,
    string RepoUrl,
    string DefaultBranch,
    string Root,
    Dictionary<string, int> Roles,
    int Pending,
    int InProgress,
    int Completed,
    int Failed,
    IReadOnlyList<ProjectsSlotRow> Slots);

public sealed record ProjectsSlotRow(string ProjectId, string Role, int InFlight, int Max);

public sealed record AddProjectRequestBody(string Id, string Name, string RepoUrl, string? DefaultBranch);
public sealed record AddProjectResponseBody(ProjectRecord Project, object? ClonedInfo, string? Warning);
public sealed record ProjectRecord(
    string Id, string Name, string RepoUrl, string DefaultBranch,
    string? LocalPath, DateTime CreatedAt, DateTime UpdatedAt,
    DateTime? LastSyncedAt, string? LastSyncError);

public static class ProjectsActions
{
    public sealed record LoadProjectsAction();
    public sealed record ProjectsLoadedAction(IReadOnlyList<ProjectsEndpointRow> Projects);
    public sealed record ProjectsLoadFailedAction(string Error);

    public sealed record AddProjectAction(
        string Id, string Name, string RepoUrl, string? DefaultBranch);
    public sealed record AddProjectSubmittingAction();
    public sealed record AddProjectSucceededAction(string Id);
    public sealed record AddProjectFailedAction(string Error);
    public sealed record AddProjectDismissErrorAction();
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

    public async Task<AddProjectResponseBody> AddAsync(AddProjectRequestBody body, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/api/projects/", body, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"POST /api/projects returned {(int)resp.StatusCode}: {raw}");
        }
        return System.Text.Json.JsonSerializer.Deserialize<AddProjectResponseBody>(raw)
            ?? throw new InvalidOperationException("Empty response body");
    }
}
