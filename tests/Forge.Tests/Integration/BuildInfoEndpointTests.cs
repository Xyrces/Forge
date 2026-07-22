using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

public class BuildInfoEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public BuildInfoEndpointTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-buildinfo-ep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        BuildInfoEndpoints.MapBuildInfoEndpoint(app);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildInfo_ReturnsOk_WithVersionAndFramework()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(body.Framework));
    }

    private static int GetEphemeralPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class BuildInfoShape
    {
        public string InformationalVersion { get; set; } = string.Empty;
        public string Framework { get; set; } = string.Empty;
    }
}
