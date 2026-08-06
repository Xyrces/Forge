using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 3 memory HTTP endpoints. Round-trips through the real
/// DashboardHost wiring against a fresh per-test DB.
/// </summary>
public class MemoryEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MemoryStore _memory;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public MemoryEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-mem-api-{Guid.NewGuid():N}.db");
        _ = new IssueStore(_dbPath);
        _memory = new MemoryStore(_dbPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(_dbPath) ?? Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        MemoryEndpoints.MapMemoryEndpoints(app, _memory, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _memory.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
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
    public async Task Get_Empty_ReturnsEmptyArray()
    {
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/memory");
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Post_Valid_Returns201()
    {
        var resp = await _client.PostAsJsonAsync("/api/memory",
            new { key = "k1", body = "b1" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("k1", j.GetProperty("key").GetString());
        Assert.Equal("b1", j.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Post_EmptyKey_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/memory",
            new { key = "", body = "b" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_BadTtl_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/memory",
            new { key = "k", body = "b", ttlDays = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_PrefixFilter_ReturnsMatchingOnly()
    {
        await _client.PostAsJsonAsync("/api/memory", new { key = "coding-style/a", body = "1" });
        await _client.PostAsJsonAsync("/api/memory", new { key = "coding-style/b", body = "2" });
        await _client.PostAsJsonAsync("/api/memory", new { key = "ops/x", body = "3" });

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/memory?prefix=coding-style/");
        Assert.Equal(2, list.GetArrayLength());
    }

    [Fact]
    public async Task Delete_Existing_Returns204()
    {
        await _client.PostAsJsonAsync("/api/memory", new { key = "tmp", body = "x" });
        var del = await _client.DeleteAsync("/api/memory/tmp");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/memory");
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Delete_Missing_Returns404()
    {
        var del = await _client.DeleteAsync("/api/memory/never-existed");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }
}


