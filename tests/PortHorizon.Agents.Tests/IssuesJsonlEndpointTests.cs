using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Tests.Integration;
using Xunit;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// Phase 4 endpoint tests: GET /api/issues.jsonl streams the JSONL
/// mirror file; GET /api/issues.jsonl/path returns the absolute path.
/// </summary>
public class IssuesJsonlEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _jsonlPath;
    private readonly IssueStore _issues;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public IssuesJsonlEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-jsonl-api-{Guid.NewGuid():N}.db");
        _jsonlPath = Path.Combine(Path.GetTempPath(), $"ph-jsonl-api-{Guid.NewGuid():N}.jsonl");
        _issues = new IssueStore(_dbPath);
        File.WriteAllText(_jsonlPath, "{\"id\":\"task-1\"}\n{\"id\":\"task-2\"}\n");

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        IssuesJsonlEndpoints.MapIssuesJsonlEndpoints(app, _jsonlPath, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { File.Delete(_jsonlPath); } catch { }
    }

    private static int GetEphemeralPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task GetIssuesJsonl_ReturnsNdjson()
    {
        var resp = await _client.GetAsync("/api/issues.jsonl");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/x-ndjson", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("{\"id\":\"task-1\"}\n{\"id\":\"task-2\"}\n", body);
    }

    [Fact]
    public async Task GetIssuesJsonlPath_ReturnsAbsolutePath()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/api/issues.jsonl/path");
        Assert.Equal(_jsonlPath, json.GetProperty("path").GetString());
    }
}