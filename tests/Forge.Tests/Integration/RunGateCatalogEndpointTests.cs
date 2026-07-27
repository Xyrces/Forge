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
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-gate-cat-{Guid.NewGuid():N}");
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

    private sealed record CatalogResponse(
        string Checkpoint,
        string Source,
        List<GateInfo> Gates);

    private sealed record GateInfo(
        string Name,
        string? Kind,
        string? Description,
        string Source);
}
