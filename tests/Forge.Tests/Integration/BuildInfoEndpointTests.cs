using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Dashboard;
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

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{GetEphemeralPort()}");
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
    public async Task BuildInfo_ReturnsOkWithJson()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(body.Framework));
    }

    [Fact]
    public async Task BuildInfo_InformationalVersionMatchesAssembly()
    {
        var asm = typeof(BuildInfoEndpoints).Assembly;
        var expected = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fallback = asm.GetName().Version?.ToString() ?? "0.0.0";

        var resp = await _client.GetAsync("/api/meta/buildinfo");
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();

        // CI builds append a "+sha" suffix to the informational version.
        // Compare modulo that suffix so the test stays stable across
        // local + CI builds.
        var expectedPrefix = (expected ?? fallback).Split('+')[0];
        var actualPrefix = body!.InformationalVersion.Split('+')[0];
        Assert.Equal(expectedPrefix, actualPrefix);
    }

    [Fact]
    public async Task BuildInfo_FrameworkIsNetDescription()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotNull(body);
        Assert.Contains(".NET", body!.Framework);
    }

    [Fact]
    public async Task BuildInfo_UsesCamelCaseWireFormat()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        var raw = await resp.Content.ReadAsStringAsync();
        // camelCase keys from DashboardJson.Options.
        Assert.Contains("informationalVersion", raw);
        Assert.Contains("framework", raw);
        Assert.DoesNotContain("InformationalVersion", raw);
        Assert.DoesNotContain("Framework", raw);
    }

    [Fact]
    public async Task BuildInfo_PostReturnsMethodNotAllowed()
    {
        var resp = await _client.PostAsync("/api/meta/buildinfo", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class BuildInfoShape
    {
        public string InformationalVersion { get; set; } = "";
        public string Framework { get; set; } = "";
    }
}
