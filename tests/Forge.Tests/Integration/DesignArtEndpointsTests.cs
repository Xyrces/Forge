using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class DesignArtEndpointsTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly DesignArtifactStore _designs;
    private readonly ArtOutputStore _arts;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public DesignArtEndpointsTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("design-art");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _designs = new DesignArtifactStore(_dbPath);
        _arts = new ArtOutputStore(_dbPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        DesignArtEndpoints.MapDesignArtEndpoints(app, _designs, _arts, null, null,
            NullLogger<DashboardHost>.Instance);
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
    public async Task Designs_NoProject_ReturnsEmpty()
    {
        var resp = await _client.GetAsync("/api/designs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<DesignShape>>();
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Art_NoProject_ReturnsEmpty()
    {
        var resp = await _client.GetAsync("/api/art-output");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<ArtShape>>();
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Designs_FilteredByProject_ReturnsEmptyForUnknown()
    {
        var resp = await _client.GetAsync("/api/designs?projectId=nope");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<DesignShape>>();
        Assert.Empty(list!);
    }

    [Fact]
    public async Task MeshyTasks_NoRunStore_ReturnsEmpty()
    {
        var resp = await _client.GetAsync("/api/artist/runs/1/meshy-tasks");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<MeshyShape>>();
        Assert.Empty(list!);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class DesignShape
    {
        public string Id { get; set; } = "";
        public string SpecId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
    }

    public sealed class ArtShape
    {
        public string Id { get; set; } = "";
        public string SpecId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string FileUrl { get; set; } = "";
    }

    public sealed class MeshyShape
    {
        public string Id { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
