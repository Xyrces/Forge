using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class MetaEndpointsTests : IDisposable
{
    private readonly string _workDir;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public MetaEndpointsTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-meta-{Guid.NewGuid():N}");
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
        app.MapGet("/api/hello", () => Results.Json(new { hello = "world" }));
        app.MapGet("/api/meta/endpoints", (HttpContext ctx) =>
        {
            var endpoints = ctx.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints
                .Select(e =>
                {
                    var routePattern = e.Metadata.OfType<RoutePattern>().FirstOrDefault();
                    return routePattern?.RawText ?? e.DisplayName;
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Json(new { endpoints = endpoints });
        });

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
        Assert.Contains(body!.Endpoints, e => e.Contains("/api/hello", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(body.Endpoints, e => e.Contains("/api/meta/endpoints", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MetaResponse(string[] Endpoints);

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint!).Port;
        listener.Stop();
        return port;
    }
}
