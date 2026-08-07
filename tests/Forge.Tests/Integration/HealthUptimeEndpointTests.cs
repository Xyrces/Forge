using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class HealthUptimeEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly IHost _host;
    private readonly Uri _baseAddress;
    private readonly HttpClient _client;

    public HealthUptimeEndpointTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("uptime-ep");
        Directory.CreateDirectory(_workDir);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        HealthEndpoint.MapHealthEndpoint(app, new DefaultHealthSnapshotFactory());
        _host = app;
        _host.Start();
        _baseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/");
        _client = new HttpClient { BaseAddress = _baseAddress };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Uptime_Returns_Ok_With_UptimeMs_And_UtcTimestamp()
    {
        var resp = await _client.GetAsync("/api/health/uptime");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<UptimeShape>();
        Assert.NotNull(body);
        Assert.True(body!.UptimeMs >= 0);
        Assert.True(body.UtcTimestamp > DateTime.UtcNow.AddSeconds(-10));
    }

    [Fact]
    public async Task Uptime_Response_Is_Valid_Json_With_Expected_Properties()
    {
        using var resp = await _client.GetAsync("/api/health/uptime");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("uptimeMs", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("utcTimestamp", json, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetEphemeralPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed record UptimeShape(long UptimeMs, DateTime UtcTimestamp);
}
