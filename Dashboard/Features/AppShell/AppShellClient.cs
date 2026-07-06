using System.Net.Http.Json;

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

public sealed class AppShellClient
{
    private readonly HttpClient _http;

    public AppShellClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ActiveSprintDto?> GetActiveSprintAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<ActiveSprintDto>("/api/sprints/active", ct);
        }
        catch (HttpRequestException)
        {
            return null;
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