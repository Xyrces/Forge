using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// /api/agent-runs DTO mapping (v25): run rows expose phase +
/// resumedSession on both the list and detail payloads. Plus the
/// /api/memory session-key exclusion (session blobs are machine
/// state, not operator memory).
/// </summary>
public class AgentRunEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public AgentRunEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-runs-api-{Guid.NewGuid():N}.db");
        _ = new IssueStore(_dbPath);
        _runs = new AgentRunStore(_dbPath);
        _memory = new MemoryStore(_dbPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(_dbPath) ?? Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        AgentRunEndpoints.MapAgentRunEndpoints(app, _runs);
        MemoryEndpoints.MapMemoryEndpoints(app, _memory, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static int GetEphemeralPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task List_IncludesPhaseAndResumedSession()
    {
        await _runs.StartAsync("run-p1", "task-1", "CoreDev", "m", resumedSession: true);
        await _runs.UpdateProgressAsync("run-p1", 3, 1, 100, phase: "verifying 1/3");

        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/agent-runs");
        var row = doc.GetProperty("active").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "run-p1");
        Assert.Equal("verifying 1/3", row.GetProperty("phase").GetString());
        Assert.True(row.GetProperty("resumedSession").GetBoolean());
    }

    [Fact]
    public async Task Detail_IncludesPhaseAndResumedSession()
    {
        await _runs.StartAsync("run-p2", "task-2", "Reviewer", "m", resumedSession: true);
        await _runs.UpdateProgressAsync("run-p2", 1, 0, 10, phase: "reviewing");

        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/agent-runs/run-p2");
        var view = doc.GetProperty("view");
        Assert.Equal("reviewing", view.GetProperty("phase").GetString());
        Assert.True(view.GetProperty("resumedSession").GetBoolean());
    }

    [Fact]
    public async Task MemoryList_ExcludesSessionKeys()
    {
        await _memory.RememberAsync("vision/master", "the vision");
        await _memory.RememberAsync("session/_/task-9/CoreDev", "{\"big\":\"blob\"}");

        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/memory");
        var keys = doc.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        Assert.Contains("vision/master", keys);
        Assert.DoesNotContain("session/_/task-9/CoreDev", keys);
    }
}
