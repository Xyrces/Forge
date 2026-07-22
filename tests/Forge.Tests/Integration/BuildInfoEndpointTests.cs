using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
/// task-65: Integration tests for GET /api/meta/buildinfo. Boots a real
/// WebApplication on an ephemeral loopback port (same pattern as the
/// other Dashboard endpoint test suites) and exercises the endpoint
/// end-to-end:
///   - happy path returns informationalVersion + framework with a
///     JSON body shaped like {informationalVersion, framework}.
///   - informationalVersion is resolved through the
///     AssemblyInformationalVersionAttribute / AssemblyName.Version /
///     "0.0.0" fallback chain and is never null/whitespace.
///   - framework is resolved through RuntimeInformation.FrameworkDescription
///     and is never null/whitespace.
///   - read-only contract: POST returns 405 (only the GET handler is mapped).
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
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
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
    public async Task GetBuildInfo_ReturnsVersionAndFramework()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(body.Framework));
    }

    [Fact]
    public async Task GetBuildInfo_InformationalVersionMatchesAssemblyAttribute()
    {
        // The endpoint resolves the version from the entry assembly — it
        // must agree with what's actually on disk. For the test host that
        // is Forge.Tests.dll; both attribute-fallback chains should agree.
        var asm = typeof(BuildInfoEndpointTests).Assembly;
        var attr = asm.GetCustomAttributes(
            typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        var expected = attr.Length == 0
            ? asm.GetName().Version?.ToString() ?? "0.0.0"
            : ((System.Reflection.AssemblyInformationalVersionAttribute)attr[0]).InformationalVersion;
        if (string.IsNullOrWhiteSpace(expected))
        {
            expected = "0.0.0";
        }

        var resp = await _client.GetAsync("/api/meta/buildinfo");
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        // InformationalVersionAttribute allows extra "+sha" suffix appended
        // by the build pipeline; only the leading semver portion has to match.
        var actualPrefix = body!.InformationalVersion.Split('+')[0];
        var expectedPrefix = expected.Split('+')[0];
        Assert.StartsWith(actualPrefix, expected);
        Assert.StartsWith(expectedPrefix, body.InformationalVersion);
        // ... but our endpoint should never return an empty string.
        Assert.False(string.IsNullOrWhiteSpace(body.InformationalVersion));
    }

    [Fact]
    public async Task GetBuildInfo_FrameworkIsNonEmpty()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotEqual("Unknown", body!.Framework);
        Assert.Contains(".NET", body.Framework);
    }

    [Fact]
    public async Task PostBuildInfo_ReturnsMethodNotAllowed()
    {
        // Only MapGet is registered — POST must surface as 405 (not silently
        // 200/empty body) so callers can tell it's read-only.
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
