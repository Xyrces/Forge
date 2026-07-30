using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// End-to-end integration test for <see cref="MetaEndpoints.MapMetaEndpoints"/>.
/// Boots a real <see cref="WebApplication"/> on an ephemeral loopback port so
/// the production endpoint registration is exercised through the full HTTP
/// pipeline (routing -> EndpointDataSource -> minimal-API handler).
/// </summary>
public class MetaEndpointsTests : IDisposable
{
    private readonly string _workDir;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public MetaEndpointsTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("meta");
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

        // Fixture routes that exercise both the /api/* filter and the
        // route-pattern metadata path in production. The /internal/foo
        // route must NOT appear in the response.
        app.MapGet("/api/hello", () => Results.Json(new { hello = "world" }));
        app.MapGet("/api/widgets/{id}", (string id) => Results.Json(new { id }));
        app.MapGet("/internal/foo", () => Results.Json(new { foo = true }));

        // Production endpoint under test. NOT a duplicate registration —
        // this is the code the test asserts behavior for.
        MetaEndpoints.MapMetaEndpoints(app);

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
    public async Task MetaEndpoints_ReturnsRegisteredApiPatterns()
    {
        var resp = await _client.GetAsync("/api/meta/endpoints");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<MetaResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body!.Endpoints);

        // The /api/* patterns registered as fixtures must be present.
        Assert.Contains(body.Endpoints, e => e.Contains("/api/hello", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(body.Endpoints, e => e.Contains("/api/widgets", StringComparison.OrdinalIgnoreCase));

        // The endpoint under test must list itself.
        Assert.Contains(body.Endpoints, e => e.Contains("/api/meta/endpoints", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetaEndpoints_FiltersOutNonApiRoutes()
    {
        var resp = await _client.GetAsync("/api/meta/endpoints");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<MetaResponse>();
        Assert.NotNull(body);

        // /internal/foo must be excluded by the production /api/* filter.
        Assert.DoesNotContain(body!.Endpoints, e => e.Contains("/internal/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetaEndpoints_GeneratedAt_IsRoundTrippableUtc()
    {
        var before = DateTime.UtcNow.AddMinutes(-1);
        var resp = await _client.GetAsync("/api/meta/endpoints");
        var after = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<MetaResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.GeneratedAt));

        var parsed = DateTimeOffset.Parse(
            body.GeneratedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.InRange(parsed.UtcDateTime, before, after);
    }

    [Fact]
    public async Task MetaEndpoints_PostReturns405()
    {
        // Read-only contract: only MapGet is registered, so any non-GET
        // method must auto-405.
        var resp = await _client.PostAsync("/api/meta/endpoints", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task MetaEndpoints_ResponseShapeIsCamelCase()
    {
        var resp = await _client.GetAsync("/api/meta/endpoints");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("endpoints", out _));
        Assert.True(root.TryGetProperty("generatedAt", out _));
    }

    private sealed record MetaResponse(string[] Endpoints, string GeneratedAt);

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint!).Port;
        listener.Stop();
        return port;
    }
}
