using System.Diagnostics;
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

public class AppShellEndpointsTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly SprintStore _sprints;
    private readonly MemoryStore _memory;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public AppShellEndpointsTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-appshell-ep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        var designArtifacts = new DesignArtifactStore(_dbPath);
        var holder = new SpecStoreHolder();
        _specs = new SpecStore(_issues, designArtifacts: designArtifacts);
        holder.Set(_specs);
        _sprints = new SprintStore(_issues);
        _memory = new MemoryStore(_dbPath);

        var port = GetEphemeralPort();
        // Pass the test's temp dir as contentRoot via WebApplicationOptions
        // -- setting it via UseContentRoot after CreateBuilder throws
        // NotSupportedException in net10.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        AppShellEndpoints.MapAppShellEndpoints(app, _issues, _sprints, _specs, _memory,
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
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Heartbeat_ReturnsHealthy()
    {
        var resp = await _client.GetAsync("/api/health/heartbeat");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HeartbeatShape>();
        Assert.NotNull(body);
        Assert.Equal("healthy", body!.Status);
        Assert.True(body.At > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Uptime_ReturnsMonotonicMillisAndIsoTimestamp()
    {
        var before = Environment.TickCount64;
        var resp = await _client.GetAsync("/api/health/uptime");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<UptimeShape>();
        Assert.NotNull(body);
        Assert.True(body!.UptimeMs >= before, $"uptimeMs {body.UptimeMs} should be >= {before}");
        Assert.True(body.UptimeMs <= Environment.TickCount64 + 1000);
        var parsed = DateTime.Parse(body.UtcTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.True(parsed > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Uptime_PostReturnsMethodNotAllowed()
    {
        var resp = await _client.PostAsync("/api/health/uptime", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task BuildInfo_ReturnsNonEmptyVersionAndFramework()
    {
        var resp = await _client.GetAsync("/api/meta/buildinfo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BuildInfoShape>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(body.Framework));
    }

    [Fact]
    public async Task BuildInfo_IsReadOnly_NoPostHandler()
    {
        var resp = await _client.PostAsync("/api/meta/buildinfo", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task ActiveSprint_None_ReturnsNull()
    {
        var resp = await _client.GetAsync("/api/sprints/active");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ActiveSprintShape>();
        Assert.NotNull(body);
        Assert.Null(body!.Id);
        Assert.Null(body.Name);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyResults()
    {
        var resp = await _client.GetAsync("/api/search?q=");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SearchResultsShape>();
        Assert.NotNull(body);
        Assert.Empty(body!.Issues);
        Assert.Empty(body.Specs);
        Assert.Empty(body.Memory);
    }

    [Fact]
    public async Task Search_SingleCharQuery_ReturnsEmpty()
    {
        var resp = await _client.GetAsync("/api/search?q=a");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SearchResultsShape>();
        Assert.Empty(body!.Issues);
    }

    [Fact]
    public async Task Search_FindsMatchingIssue()
    {
        await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "Fix memory bank hot path",
            Description: "Wire the context window to the new memory store"), default);

        var resp = await _client.GetAsync("/api/search?q=memory");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SearchResultsShape>();
        Assert.NotEmpty(body!.Issues);
        Assert.Contains(body.Issues, i => i.Title.Contains("memory"));
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class HeartbeatShape
    {
        public string Status { get; set; } = "";
        public DateTime At { get; set; }
        public string? Version { get; set; }
    }

    public sealed class UptimeShape
    {
        public long UptimeMs { get; set; }
        public string UtcTimestamp { get; set; } = "";
    }

    public sealed class BuildInfoShape
    {
        public string InformationalVersion { get; set; } = "";
        public string Framework { get; set; } = "";
    }

    public sealed class ActiveSprintShape
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public sealed class SearchHitShape
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Snippet { get; set; } = "";
    }

    public sealed class SearchResultsShape
    {
        public List<SearchHitShape> Issues { get; set; } = new();
        public List<SearchHitShape> Specs { get; set; } = new();
        public List<SearchHitShape> Memory { get; set; } = new();
    }
}
