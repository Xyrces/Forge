using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// task-72: Integration tests for GET /api/meta/buildinfo.
/// </summary>
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
    public async Task GetBuildInfo_ReturnsOkWithVersionAndFramework()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(body.Framework));
    }

    [Fact]
    public async Task GetBuildInfo_UsesCamelCaseJson()
    {
        var raw = await _client.GetStringAsync("/api/meta/buildinfo");
        Assert.Contains("\"informationalVersion\"", raw);
        Assert.Contains("\"framework\"", raw);
        Assert.DoesNotContain("\"InformationalVersion\"", raw);
        Assert.DoesNotContain("\"Framework\"", raw);
    }

    [Fact]
    public async Task GetBuildInfo_VersionResolvesToAssemblyAttribute()
    {
        var asm = typeof(BuildInfoEndpointTests).Assembly;
        var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var nameVer = asm.GetName().Version?.ToString();
        var expected = attr?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(expected))
        {
            expected = nameVer;
        }
        if (string.IsNullOrWhiteSpace(expected))
        {
            expected = "0.0.0";
        }

        var body = await (await _client.GetAsync("/api/meta/buildinfo"))
            .Content.ReadFromJsonAsync<BuildInfoShape>();

        Assert.NotNull(body);
        var actualPrefix = body!.InformationalVersion.Split('+')[0];
        var expectedPrefix = expected.Split('+')[0];
        Assert.Equal(expectedPrefix, actualPrefix);
    }

    [Fact]
    public async Task GetBuildInfo_FrameworkContainsDotNet()
    {
        var body = await (await _client.GetAsync("/api/meta/buildinfo"))
            .Content.ReadFromJsonAsync<BuildInfoShape>();

        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Framework));
        Assert.NotEqual("Unknown", body.Framework);
        Assert.Contains(".NET", body.Framework);
    }

    [Fact]
    public async Task PostBuildInfo_ReturnsMethodNotAllowed()
    {
        var resp = await _client.PostAsync("/api/meta/buildinfo", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        using var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
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
