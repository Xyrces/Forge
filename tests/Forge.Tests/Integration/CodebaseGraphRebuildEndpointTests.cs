using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Codebase;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class CodebaseGraphRebuildEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly CodebaseGraphCacheStore _cache;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public CodebaseGraphRebuildEndpointTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-cbg-rebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _cache = new CodebaseGraphCacheStore(_issues);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/api/codebase-graph/rebuild", async (string repoRoot, CancellationToken ct) =>
        {
            if (!Directory.Exists(repoRoot)) return Results.NotFound();
            await _cache.ClearAsync(ct);
            return Results.Json(new { rebuilt = true });
        });
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Rebuild_ClearsCacheAndSucceeds()
    {
        await _cache.UpsertAsync(new CodebaseGraphCache(
            BuiltAt: DateTime.UtcNow, RepoSha: "abc123",
            FileCount: 10, EdgeCount: 5, DiskPath: "/tmp/x"), default);
        Assert.NotNull(await _cache.GetAsync(_workDir, default));

        var resp = await _client.PostAsync($"/api/codebase-graph/rebuild?repoRoot={_workDir}", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Null(await _cache.GetAsync(_workDir, default));
    }

    [Fact]
    public async Task Rebuild_UnknownRepo_Returns404()
    {
        var resp = await _client.PostAsync($"/api/codebase-graph/rebuild?repoRoot=C:/nope/nope/nope", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
