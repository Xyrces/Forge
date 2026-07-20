using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Tests.Integration;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P5.6: HTTP read endpoint for the memory extraction audit log.
/// Round-trips through a real ASP.NET Core host + the v13
/// migration against a fresh per-test DB.
/// </summary>
public class MemoryExtractionEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MemoryExtractionStore _extractions;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public MemoryExtractionEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"ph-memext-api-{Guid.NewGuid():N}.db");
        // Force the v13 migration by constructing an IssueStore
        // against the same DB. MemoryExtractionStore doesn't own
        // migrations, so we trigger them externally.
        _ = new IssueStore(_dbPath);
        _extractions = new MemoryExtractionStore(_dbPath);

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(_dbPath) ?? Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        MemoryEndpoints.MapExtractionEndpoints(
            app, _extractions, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _extractions.Dispose();
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
    public async Task GetExtractions_NoData_ReturnsEmptyArray()
    {
        var resp = await _client.GetAsync("api/memory/extractions/empty-task");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task GetExtractions_WithRuns_ReturnsJsonArray()
    {
        // Seed two extraction runs for the same task.
        await _extractions.RecordAsync(new ExtractionResult(
            "task-1", 1000, 2,
            new[] { "extraction/task-1/a", "extraction/task-1/b" },
            null));
        await _extractions.RecordAsync(new ExtractionResult(
            "task-1", 500, 0, Array.Empty<string>(),
            "TimeoutException: timeout"));

        var resp = await _client.GetAsync("api/memory/extractions/task-1");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(2, list.GetArrayLength());
        // First record: success.
        Assert.Equal(1000, list[0].GetProperty("sourceChars").GetInt32());
        Assert.Equal(2, list[0].GetProperty("extractedCount").GetInt32());
        Assert.Equal(2, list[0].GetProperty("persistedKeys").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("error").ValueKind);
        Assert.Equal("task-1", list[0].GetProperty("taskId").GetString());
        // Second record: error.
        Assert.Equal(0, list[1].GetProperty("extractedCount").GetInt32());
        Assert.Equal(0, list[1].GetProperty("persistedKeys").GetArrayLength());
        Assert.Equal("TimeoutException: timeout",
            list[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetExtractions_TaskIsolation()
    {
        await _extractions.RecordAsync(new ExtractionResult(
            "task-a", 100, 1, new[] { "k1" }, null));
        await _extractions.RecordAsync(new ExtractionResult(
            "task-b", 200, 2, new[] { "k2", "k3" }, null));

        var respA = await _client.GetAsync("api/memory/extractions/task-a");
        var listA = await respA.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, listA.GetArrayLength());
        Assert.Equal("task-a", listA[0].GetProperty("taskId").GetString());

        var respB = await _client.GetAsync("api/memory/extractions/task-b");
        var listB = await respB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, listB.GetArrayLength());
        Assert.Equal("task-b", listB[0].GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task GetExtractions_TimestampPopulated()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        await _extractions.RecordAsync(new ExtractionResult(
            "task-ts", 100, 1, new[] { "k" }, null));
        var after = DateTime.UtcNow.AddSeconds(1);

        var resp = await _client.GetAsync("api/memory/extractions/task-ts");
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ts = list[0].GetProperty("timestamp").GetDateTime();
        Assert.InRange(ts, before, after);
    }
}


