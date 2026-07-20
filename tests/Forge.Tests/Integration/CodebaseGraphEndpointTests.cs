using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Codebase;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Phase 2b.2 tests:
/// - GET /api/codebase-graph?repoRoot=... round-trip
/// - SSE event stream emits intake events end-to-end
/// </summary>
public class CodebaseGraphEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _repoRoot;
    private readonly IssueStore _issues;
    private readonly ICodebaseGraphCacheStore _cache;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public CodebaseGraphEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-graph-api-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _cache = new CodebaseGraphCacheStore(_issues);

        _repoRoot = Path.Combine(Path.GetTempPath(), $"ph-graph-rep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);

        _host = BuildHost();
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
    }

    private IHost BuildHost()
    {
        var port = GetEphemeralPort();
        var workDir = Path.GetTempPath();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        var graphBuilder = new DotnetCodebaseGraphBuilder();
        CodebaseGraphEndpoints.MapCodebaseGraphEndpoints(
            app, graphBuilder, _cache, _issues, NullLogger<DashboardHost>.Instance);
        return app;
    }

    private static int GetEphemeralPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void SeedCsproj(string module)
    {
        var csproj = Path.Combine(_repoRoot, module + ".csproj");
        File.WriteAllText(csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
    }

    private void SeedCs(string relPath, params string[] usings)
    {
        var dir = Path.GetDirectoryName(relPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(Path.Combine(_repoRoot, dir));
        var usingLines = string.Join("\n", usings.Select(u => "using " + u + ";"));
        var content = "namespace X;\n\n" + usingLines + "\n\npublic class A { }\n";
        File.WriteAllText(Path.Combine(_repoRoot, relPath), content);
    }

    [Fact]
    public async Task Refresh_ColdBuild_ReturnsGraph()
    {
        SeedCsproj("Auth");
        SeedCs("Auth/Auth.cs", "Foo.Bar");

        var resp = await _client.GetAsync($"/api/codebase-graph?repoRoot={Uri.EscapeDataString(_repoRoot)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var graph = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_repoRoot, graph.GetProperty("repoRoot").GetString());
        Assert.Equal(1, graph.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, graph.GetProperty("importCount").GetInt32());
        Assert.Equal(0, graph.GetProperty("projectEdgeCount").GetInt32());
    }

    [Fact]
    public async Task Refresh_MissingRepoRoot_Returns400()
    {
        var resp = await _client.GetAsync("/api/codebase-graph");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_NonexistentDir_Returns404()
    {
        var resp = await _client.GetAsync($"/api/codebase-graph?repoRoot={Uri.EscapeDataString(@"C:\does\not\exist")}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_WarmSameSha_UsesPriorCache()
    {
        // Cold: builds the graph.
        SeedCsproj("A");
        SeedCs("A/A.cs", "Foo");
        var first = await _client.GetFromJsonAsync<JsonElement>($"/api/codebase-graph?repoRoot={Uri.EscapeDataString(_repoRoot)}");
        var firstSha = first.GetProperty("repoSha").GetString()!;
        var firstBuiltAt = first.GetProperty("builtAt").GetDateTime();

        // Warm: same sha, same built_at.
        await Task.Delay(50); // ensure clock would tick
        var second = await _client.GetFromJsonAsync<JsonElement>($"/api/codebase-graph?repoRoot={Uri.EscapeDataString(_repoRoot)}");
        Assert.Equal(firstSha, second.GetProperty("repoSha").GetString());
        Assert.Equal(firstBuiltAt, second.GetProperty("builtAt").GetDateTime());
    }
}

