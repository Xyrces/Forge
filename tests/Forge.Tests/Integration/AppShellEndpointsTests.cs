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

    [Fact]
    public async Task Uptime_ReturnsMonotonicUptimeAndUtcTimestamp()
    {
        var resp = await _client.GetAsync("/api/health/uptime");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<UptimeShape>();
        Assert.NotNull(body);

        // uptimeMs: monotonic, non-negative, bounded by the live
        // TickCount64 + a small jitter allowance for the gap
        // between server capture and test assertion.
        var now = Environment.TickCount64;
        Assert.True(body!.UptimeMs >= 0);
        Assert.True(body.UptimeMs <= now + 1000,
            $"uptimeMs {body.UptimeMs} should be within TickCount64 ({now}) + 1000ms jitter");

        // utcTimestamp: round-trippable ISO-8601 UTC, anchored to
        // "now" within ±1 minute.
        var ts = DateTime.Parse(body.UtcTimestamp, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, ts.Kind);
        var skew = (DateTime.UtcNow - ts).Duration();
        Assert.True(skew < TimeSpan.FromMinutes(1),
            $"timestamp skew {skew} exceeds 1 minute");
    }

    [Fact]
    public async Task Uptime_SecondCallHasNonDecreasingUptime()
    {
        var first = await _client.GetFromJsonAsync<UptimeShape>("/api/health/uptime");
        Assert.NotNull(first);
        // Sleep a small amount and re-read; monotonicity means the
        // second reading must be >= the first.
        await Task.Delay(50);
        var second = await _client.GetFromJsonAsync<UptimeShape>("/api/health/uptime");
        Assert.NotNull(second);
        Assert.True(second!.UptimeMs >= first!.UptimeMs,
            $"second uptimeMs {second.UptimeMs} < first {first.UptimeMs}");
    }

    [Fact]
    public async Task Uptime_PostReturnsMethodNotAllowed()
    {
        // Read-only contract: POST must auto-405 from the
        // minimal-API router since we only registered MapGet.
        var resp = await _client.PostAsync("/api/health/uptime", content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
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

    public sealed class UptimeShape
    {
        public long UptimeMs { get; set; }
        public string UtcTimestamp { get; set; } = "";
    }
}