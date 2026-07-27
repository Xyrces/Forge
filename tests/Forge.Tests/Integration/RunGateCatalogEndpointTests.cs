using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forge.Agents.Gates;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class RunGateCatalogEndpointTests : IDisposable
{
    private readonly string _memoryDbPath;
    private readonly MemoryStore? _memory;
    private readonly GateOptions _options;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public RunGateCatalogEndpointTests()
    {
        _memoryDbPath = Path.Combine(Path.GetTempPath(), $"ph-rgc-ep-{Guid.NewGuid():N}.db");
        // Initialize schema via IssueStore (creates memory table as part of v7+)
        _ = new IssueStore(_memoryDbPath);
        _memory = new MemoryStore(_memoryDbPath);
        _options = new GateOptions();

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, port));

        var app = builder.Build();
        RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(app, _options, _memory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _memory?.Dispose();
        try { File.Delete(_memoryDbPath); } catch { }
        try { File.Delete(_memoryDbPath + "-wal"); } catch { }
        try { File.Delete(_memoryDbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Get_ReturnsGateCatalog_ForKnownCheckpoint()
    {
        var resp = await _client.GetAsync("/api/gates/preImplementation");
        var rawBody = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK,
            $"Expected OK but got {resp.StatusCode}: {rawBody}");

        var parsed = JsonSerializer.Deserialize<CatalogResponse>(rawBody, DashboardJson.Options);
        Assert.NotNull(parsed);
        Assert.Equal("preImplementation", parsed!.Checkpoint);
        Assert.Equal("builtin_default", parsed.Source);
        Assert.NotEmpty(parsed.Gates);

        Assert.Contains(parsed.Gates, g => g.Name == "plan-schema");
        Assert.Contains(parsed.Gates, g => g.Name == "plan-territory");
        Assert.Contains(parsed.Gates, g => g.Name == "plan-llm-review");

        foreach (var gate in parsed.Gates)
        {
            Assert.NotNull(gate.Kind);
            Assert.NotNull(gate.Description);
        }

        Assert.Empty(parsed.UnknownNames);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_ForUnknownCheckpoint()
    {
        var resp = await _client.GetAsync("/api/gates/unknownCheckpoint");
        var rawBody = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.NotFound,
            $"Expected NotFound but got {resp.StatusCode}: {rawBody}");
    }

    [Fact]
    public async Task Put_SavesOverride_AndGetReturnsIt()
    {
        var gates = new[] { "plan-schema", "plan-territory" };
        var putResp = await _client.PutAsJsonAsync("/api/gates/preImplementation",
            new RunGateCatalogEndpoints.PutGateOverrideRequest(gates));
        var putRaw = await putResp.Content.ReadAsStringAsync();
        Assert.True(putResp.StatusCode == HttpStatusCode.OK,
            $"Expected OK but got {putResp.StatusCode}: {putRaw}");

        var putBody = JsonSerializer.Deserialize<PutResponse>(putRaw, DashboardJson.Options);
        Assert.NotNull(putBody);
        Assert.Equal("preImplementation", putBody!.Checkpoint);

        var getResp = await _client.GetAsync("/api/gates/preImplementation");
        var getRaw = await getResp.Content.ReadAsStringAsync();
        Assert.True(getResp.StatusCode == HttpStatusCode.OK,
            $"Expected OK but got {getResp.StatusCode}: {getRaw}");

        var getBody = JsonSerializer.Deserialize<CatalogResponse>(getRaw, DashboardJson.Options);
        Assert.NotNull(getBody);
        Assert.Equal("db_override", getBody!.Source);
        Assert.Equal(2, getBody.Gates.Count);
        Assert.Equal("plan-schema", getBody.Gates[0].Name);
        Assert.Equal("plan-territory", getBody.Gates[1].Name);
    }

    [Fact]
    public async Task Put_EmptyGates_Returns400()
    {
        var putResp = await _client.PutAsJsonAsync("/api/gates/preImplementation",
            new RunGateCatalogEndpoints.PutGateOverrideRequest(Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.BadRequest, putResp.StatusCode);
    }

    [Fact]
    public async Task Put_NullGates_Returns400()
    {
        var putResp = await _client.PutAsJsonAsync("/api/gates/preImplementation",
            new { gates = (string[]?)null });
        Assert.Equal(HttpStatusCode.BadRequest, putResp.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesOverride()
    {
        await _client.PutAsJsonAsync("/api/gates/preImplementation",
            new RunGateCatalogEndpoints.PutGateOverrideRequest(new[] { "plan-schema" }));

        var getBefore = await _client.GetFromJsonAsync<CatalogResponse>("/api/gates/preImplementation");
        Assert.NotNull(getBefore);
        Assert.Equal("db_override", getBefore!.Source);

        var delResp = await _client.DeleteAsync("/api/gates/preImplementation");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await _client.GetFromJsonAsync<CatalogResponse>("/api/gates/preImplementation");
        Assert.NotNull(getAfter);
        Assert.Equal("builtin_default", getAfter!.Source);
    }

    [Fact]
    public async Task Delete_UnknownCheckpoint_Returns404()
    {
        var delResp = await _client.DeleteAsync("/api/gates/nonexistent");
        var rawBody = await delResp.Content.ReadAsStringAsync();
        Assert.True(delResp.StatusCode == HttpStatusCode.NotFound,
            $"Expected NotFound but got {delResp.StatusCode}: {rawBody}");
    }

    [Fact]
    public async Task Get_ReturnsUnknownNames_ForUnrecognizedGate()
    {
        await _client.PutAsJsonAsync("/api/gates/preImplementation",
            new RunGateCatalogEndpoints.PutGateOverrideRequest(new[] { "plan-schema", "bogus-gate" }));

        var resp = await _client.GetAsync("/api/gates/preImplementation");
        var rawBody = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK,
            $"Expected OK but got {resp.StatusCode}: {rawBody}");

        var body = JsonSerializer.Deserialize<CatalogResponse>(rawBody, DashboardJson.Options);
        Assert.NotNull(body);
        Assert.Contains("bogus-gate", body!.UnknownNames);
        Assert.DoesNotContain("plan-schema", body.UnknownNames);
    }

    [Fact]
    public async Task Post_Returns405()
    {
        using var postResp = await _client.PostAsync("/api/gates/preImplementation", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postResp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed record CatalogResponse(
        string Checkpoint,
        string Source,
        List<GateEntry> Gates,
        List<string> UnknownNames);

    public sealed record GateEntry(string Name, string? Kind, string? Description, string Source);

    public sealed record PutResponse(string Checkpoint, string[] Gates);
}
