using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests.Integration;

public class OpsEndpointsTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly MemoryStore _memory;
    private readonly MemoryExtractionStore _extractions;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public OpsEndpointsTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("ops-ep");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _memory = new MemoryStore(_dbPath);
        _extractions = new MemoryExtractionStore(_dbPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        MemoryEndpoints.MapMemoryEndpoints(app, _memory, NullLogger<DashboardHost>.Instance);
        MemoryEndpoints.MapExtractionEndpoints(app, _extractions, NullLogger<DashboardHost>.Instance);
        OpsEndpoints.MapOpsEndpoints(app, null,
            new HeadroomOptions { Enabled = false, ProxyBaseUrl = "" },
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
        _extractions.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecoveryPolicies_ReturnsFourPolicies()
    {
        var resp = await _client.GetAsync("/api/recovery/policies");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<PolicyShape>>();
        Assert.NotNull(list);
        Assert.Equal(4, list!.Count);
        Assert.Contains(list, p => p.Id == "replay");
        Assert.Contains(list, p => p.Id == "reclaim");
        Assert.Contains(list, p => p.Id == "left-alone");
        Assert.Contains(list, p => p.Id == "manual");
    }

    [Fact]
    public async Task HeadroomStats_Disabled_ReturnsEnabledFalse()
    {
        var resp = await _client.GetAsync("/api/cost/headroom");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HeadroomShape>();
        Assert.NotNull(body);
        Assert.False(body!.Enabled);
    }

    [Fact]
    public async Task HeadroomStats_EnabledUnreachable_ReturnsError()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        OpsEndpoints.MapOpsEndpoints(app, null,
            new HeadroomOptions { Enabled = true, ProxyBaseUrl = "http://127.0.0.1:1" },
            NullLogger<DashboardHost>.Instance);
        await app.StartAsync();
        try
        {
            using var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}/") };
            var resp = await c.GetAsync("/api/cost/headroom");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<HeadroomShape>();
            Assert.True(body!.Enabled);
            Assert.False(body.ProxyReachable);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task MemoryExtractions_GlobalList_ReturnsNewestFirst()
    {
        await _extractions.RecordAsync(new ExtractionResult(
            IssueId: "T-1", SourceChars: 100, ExtractedCount: 2,
            PersistedKeys: new List<string> { "a", "b" }, Error: null), default);
        await _extractions.RecordAsync(new ExtractionResult(
            IssueId: "T-2", SourceChars: 200, ExtractedCount: 1,
            PersistedKeys: new List<string> { "c" }, Error: null), default);

        var resp = await _client.GetAsync("/api/memory/extractions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<ExtractionShape>>();
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public async Task MemoryExtractions_PerTask_ReturnsOnlyThatTask()
    {
        await _extractions.RecordAsync(new ExtractionResult(
            IssueId: "T-1", SourceChars: 100, ExtractedCount: 1,
            PersistedKeys: new List<string> { "a" }, Error: null), default);
        await _extractions.RecordAsync(new ExtractionResult(
            IssueId: "T-2", SourceChars: 200, ExtractedCount: 1,
            PersistedKeys: new List<string> { "b" }, Error: null), default);

        var resp = await _client.GetAsync("/api/memory/extractions/T-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<ExtractionShape>>();
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("T-1", list[0].TaskId);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class PolicyShape
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string When { get; set; } = "";
        public string Action { get; set; } = "";
        public string Why { get; set; } = "";
    }

    public sealed class HeadroomShape
    {
        public bool Enabled { get; set; }
        public bool ProxyReachable { get; set; }
        public string? ProxyBaseUrl { get; set; }
        public long? CallsLast1h { get; set; }
        public long? SavedInputTokens { get; set; }
        public double? SavedPct { get; set; }
        public string? Error { get; set; }
    }

    public sealed class ExtractionShape
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string TaskId { get; set; } = "";
        public int SourceChars { get; set; }
        public int ExtractedCount { get; set; }
        public List<string> PersistedKeys { get; set; } = new();
        public string? Error { get; set; }
    }
}
