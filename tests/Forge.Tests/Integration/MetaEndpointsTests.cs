using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // Mount a couple of stub routes alongside the endpoint under test
        // so we can assert both that the response is a JSON array and
        // that it surfaces patterns pulled from EndpointDataSource.
        app.MapGet("/api/hello", () => Results.Json(new { hello = "world" }));
        app.MapGet("/api/things/{id}", (string id) => Results.Json(new { id }));

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
    public async Task MetaEndpoints_ReturnsJsonArrayOfApiPatterns()
    {
        var resp = await _client.GetAsync("/api/meta/endpoints");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<List<string>>();
        Assert.NotNull(body);

        // JSON array of strings.
        Assert.NotEmpty(body!);

        // Only /api/* patterns appear.
        Assert.All(body, p => Assert.StartsWith("/api/", p));

        // Sorted + deduplicated.
        Assert.Equal(body.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), body);

        // Both stub routes + the endpoint itself are present.
        Assert.Contains("/api/hello", body);
        Assert.Contains("/api/meta/endpoints", body);
        Assert.Contains("/api/things/{id}", body);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
