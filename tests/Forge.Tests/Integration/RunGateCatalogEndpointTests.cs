using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents.Gates;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class RunGateCatalogEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly MemoryStore _memory;
    private readonly GateOptions _gateOptions;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public RunGateCatalogEndpointTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("gate-cat");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        // Bootstrap the schema so memory table exists
        new IssueStore(_dbPath).Dispose();
        _memory = new MemoryStore(_dbPath);
        _gateOptions = new GateOptions();

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();

        RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(_app, _gateOptions, _memory, NullLogger<RunGatePipeline>.Instance);

        _app.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().GetAwaiter().GetResult();
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Get_Returns200WithCorrectGates_ForPreImplementation()
    {
        var resp = await _client.GetAsync("/api/gates/preImplementation");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.NotNull(body);
        Assert.Equal("preImplementation", body.Checkpoint);
        Assert.Equal("builtin_default", body.Source);
        Assert.Equal(3, body.Gates.Count);

        // Order: plan-schema, plan-territory, plan-llm-review
        Assert.Equal("plan-schema", body.Gates[0].Name);
        Assert.Equal("Deterministic", body.Gates[0].Kind);
        Assert.False(string.IsNullOrWhiteSpace(body.Gates[0].Description));
        Assert.Equal("builtin_default", body.Gates[0].Source);

        Assert.Equal("plan-territory", body.Gates[1].Name);
        Assert.Equal("Deterministic", body.Gates[1].Kind);

        Assert.Equal("plan-llm-review", body.Gates[2].Name);
        Assert.Equal("Llm", body.Gates[2].Kind);
        Assert.Empty(body.UnknownNames);
    }

    [Fact]
    public async Task Get_ResolutionSourceIsBuiltinDefault_WhenNoOverrides()
    {
        var resp = await _client.GetAsync("/api/gates/preImplementation");
        var body = await resp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("builtin_default", body!.Source);
    }

    [Fact]
    public async Task Get_ResolutionSourceIsConfig_WhenGateOptionsConfigured()
    {
        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var configOptions = new GateOptions();
        configOptions.Run["preImplementation"] = new[] { "plan-schema" };
        var app = builder.Build();
        RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(app, configOptions, _memory, NullLogger<RunGatePipeline>.Instance);
        app.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var resp = await client.GetAsync("/api/gates/preImplementation");
        var body = await resp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("config", body!.Source);
        Assert.Single(body.Gates);
        Assert.Equal("plan-schema", body.Gates[0].Name);

        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Get_ResolutionSourceIsDbOverride_WhenMemoryKeySet()
    {
        var names = new[] { "plan-territory" };
        var json = System.Text.Json.JsonSerializer.Serialize(names);
        await _memory.RememberAsync("gates/run/preImplementation", json);

        var resp = await _client.GetAsync("/api/gates/preImplementation");
        var body = await resp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("db_override", body!.Source);
        Assert.Single(body.Gates);
        Assert.Equal("plan-territory", body.Gates[0].Name);
    }

    [Fact]
    public async Task Get_UnknownCheckpoint_Returns404()
    {
        var resp = await _client.GetAsync("/api/gates/bogus-checkpoint");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Returns405()
    {
        var resp = await _client.PostAsync("/api/gates/preImplementation", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }
    [Fact]
    public async Task Get_ReturnsUnknownNames_ForUnrecognizedGate()
    {
        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var configOptions = new GateOptions();
        configOptions.Run["preImplementation"] = new[] { "plan-schema", "bogus-gate" };
        var app = builder.Build();
        RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(app, configOptions, null!, NullLogger<RunGatePipeline>.Instance);
        app.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var resp = await client.GetAsync("/api/gates/preImplementation");
        var body = await resp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Contains("bogus-gate", body!.UnknownNames);
        Assert.DoesNotContain("plan-schema", body.UnknownNames);

        await app.StopAsync();
        await app.DisposeAsync();
    }



    [Fact]
    public async Task Put_OverrideWritesAndGetReturnsDbOverride()
    {
        // Arrange: override with a single gate
        var overrideGates = new[] { "plan-territory" };

        // Act: PUT the override
        var putResp = await _client.PutAsJsonAsync("/api/gates/preImplementation", new { gates = overrideGates });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Verify the PUT response body shows the overridden state
        var putBody = await putResp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.NotNull(putBody);
        Assert.Equal("preImplementation", putBody!.Checkpoint);
        Assert.Equal("db_override", putBody.Source);
        Assert.Single(putBody.Gates);
        Assert.Equal("plan-territory", putBody.Gates[0].Name);

        // Verify a subsequent GET also reflects the override
        var getResp = await _client.GetAsync("/api/gates/preImplementation");
        var getBody = await getResp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("db_override", getBody!.Source);
        Assert.Single(getBody.Gates);
        Assert.Equal("plan-territory", getBody.Gates[0].Name);
    }

    [Fact]
    public async Task Put_EmptyArray_ReturnsBadRequest()
    {
        // The endpoint rejects empty arrays with 400.
        var putResp = await _client.PutAsJsonAsync("/api/gates/preImplementation", new { gates = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, putResp.StatusCode);
    }

    [Fact]
    public async Task Delete_RevertsToBuiltinDefault()
    {
        // Arrange: first write an override
        var overrideGates = new[] { "plan-schema" };
        var putResp = await _client.PutAsJsonAsync("/api/gates/preImplementation", new { gates = overrideGates });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Verify override is in effect
        var getAfterPut = await _client.GetAsync("/api/gates/preImplementation");
        var afterPutBody = await getAfterPut.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("db_override", afterPutBody!.Source);

        // Act: DELETE to remove the override
        var delResp = await _client.DeleteAsync("/api/gates/preImplementation");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        // Verify the DELETE response body shows reverted state
        var delBody = await delResp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.NotNull(delBody);
        Assert.Equal("builtin_default", delBody!.Source);
        Assert.Equal(3, delBody.Gates.Count);

        // Verify GET also shows builtin_default again
        var getAfterDel = await _client.GetAsync("/api/gates/preImplementation");
        var afterDelBody = await getAfterDel.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("builtin_default", afterDelBody!.Source);
        Assert.Equal(3, afterDelBody.Gates.Count);
    }

    [Fact]
    public async Task Delete_UnknownCheckpoint_ReturnsNotFound()
    {
        // Act: DELETE a non-existent checkpoint (no override exists)
        var resp = await _client.DeleteAsync("/api/gates/bogus-checkpoint");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_Returns503_WhenMemoryIsNull()
    {
        // Arrange: build a separate app with null memory
        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(app, _gateOptions, null, NullLogger<RunGatePipeline>.Instance);
        app.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        // Act
        var putResp = await client.PutAsJsonAsync("/api/gates/preImplementation", new { gates = new[] { "plan-schema" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, putResp.StatusCode);

        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Delete_Returns503_WhenMemoryIsNull()
    {
        // Arrange: build a separate app with null memory
        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(app, _gateOptions, null, NullLogger<RunGatePipeline>.Instance);
        app.Start();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        // Act
        var delResp = await client.DeleteAsync("/api/gates/preImplementation");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, delResp.StatusCode);

        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Put_ThenDelete_ThenPutAgain_RoundTripWorks()
    {
        // PUT override
        var put1 = await _client.PutAsJsonAsync("/api/gates/preImplementation", new { gates = new[] { "plan-llm-review" } });
        Assert.Equal(HttpStatusCode.OK, put1.StatusCode);
        var body1 = await put1.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("db_override", body1!.Source);
        Assert.Single(body1.Gates);
        Assert.Equal("plan-llm-review", body1.Gates[0].Name);

        // DELETE override
        var del = await _client.DeleteAsync("/api/gates/preImplementation");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var bodyDel = await del.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("builtin_default", bodyDel!.Source);

        // PUT again with different gates
        var put2 = await _client.PutAsJsonAsync("/api/gates/preImplementation", new { gates = new[] { "plan-schema", "plan-territory" } });
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
        var body2 = await put2.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.Equal("db_override", body2!.Source);
        Assert.Equal(2, body2.Gates.Count);
        Assert.Equal("plan-schema", body2.Gates[0].Name);
        Assert.Equal("plan-territory", body2.Gates[1].Name);
    }

    private sealed record CatalogResponse(
        string Checkpoint,
        string Source,
        List<GateInfo> Gates,
        IReadOnlyList<string> UnknownNames);

    private sealed record GateInfo(
        string Name,
        string? Kind,
        string? Description,
        string Source);
}
