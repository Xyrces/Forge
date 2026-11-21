using System.Net.Http.Json;
using System.Text.Json;

namespace Forge.Dashboard.Features.AppShell;

public sealed class ActiveSprintDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}

public sealed class HeartbeatDto
{
    public string Status { get; init; } = "unknown";
    public DateTime At { get; init; }
}

/// <summary>Additive /api/meta/buildinfo contract; revision identity is independent of projects.</summary>
public sealed record BuildInfoDto(string InformationalVersion, string Framework,
    string? SourceRevision = null, string? CommitUrl = null);

public sealed class AppShellClient
{
    private readonly HttpClient _http;

    public AppShellClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<BuildInfoDto?> GetBuildInfoAsync(CancellationToken ct)
    {
        try
        {
            // Never scope running Forge identity to the selected target project.
            return await _http.GetFromJsonAsync<BuildInfoDto>("/api/meta/buildinfo", ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // HttpClient timeout, not caller cancellation.
        }
    }

    public async Task<ActiveSprintDto?> GetActiveSprintAsync(string? projectId, CancellationToken ct)
    {
        try
        {
            var url = projectId is null
                ? "/api/sprints/active"
                : $"/api/sprints/active?projectId={Uri.EscapeDataString(projectId)}";
            return await _http.GetFromJsonAsync<ActiveSprintDto>(url, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProjectListEntry>> ListProjectsAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ProjectListEntry>>("/api/projects/", ct)
                ?? new List<ProjectListEntry>();
        }
        catch (HttpRequestException)
        {
            return new List<ProjectListEntry>();
        }
    }

    public async Task<HeartbeatDto?> GetHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<HeartbeatDto>("/api/health/heartbeat", ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}