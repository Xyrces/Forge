using System.Net.Http.Json;
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
/// StageGates: the operator review gates at the pipeline's major
/// automatic transitions. Store semantics + the HTTP surface the
/// dashboard toggles use.
/// </summary>
public class StageGatesTests : IDisposable
{
    private readonly string _workDir;
    private readonly MemoryStore _memory;
    private readonly StageGates _gates;

    public StageGatesTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("gates");
        Directory.CreateDirectory(_workDir);
        var bootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        bootstrap.Dispose();
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));
        _gates = new StageGates(_memory);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Absent_IsOpen_HoldThenRelease_RoundTrips()
    {
        Assert.False(await _gates.IsHeldAsync(StageGates.Merge));
        await _gates.HoldAsync(StageGates.Merge);
        Assert.True(await _gates.IsHeldAsync(StageGates.Merge));
        await _gates.ReleaseAsync(StageGates.Merge);
        Assert.False(await _gates.IsHeldAsync(StageGates.Merge));
    }

    [Fact]
    public async Task Snapshot_CoversAllStages_Independently()
    {
        await _gates.HoldAsync(StageGates.Groom);
        var snap = await _gates.SnapshotAsync();
        Assert.Equal(StageGates.All.Length, snap.Count);
        Assert.True(snap[StageGates.Groom]);
        Assert.False(snap[StageGates.Design]);
        Assert.False(snap[StageGates.Sprint]);
        Assert.False(snap[StageGates.Merge]);
    }
}

/// <summary>GET /api/gates + POST hold/release round-trip.</summary>
public class GateEndpointTests : IAsyncLifetime
{
    private readonly string _workDir;
    private MemoryStore _memory = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public GateEndpointTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("gateep");
        Directory.CreateDirectory(_workDir);
    }

    public async Task InitializeAsync()
    {
        var bootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        bootstrap.Dispose();
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        GateEndpoints.MapGateEndpoints(app, new StageGates(_memory), NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        await Task.CompletedTask;
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
    public async Task Hold_ThenSnapshot_ShowsHeld_Release_Reopens()
    {
        var initial = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/gates");
        Assert.Equal("open", initial!["merge"]);

        var hold = await _client.PostAsync("/api/gates/merge/hold", null);
        hold.EnsureSuccessStatusCode();
        var held = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/gates");
        Assert.Equal("hold", held!["merge"]);
        Assert.Equal("open", held["design"]);

        var rel = await _client.PostAsync("/api/gates/merge/release", null);
        rel.EnsureSuccessStatusCode();
        var open = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/gates");
        Assert.Equal("open", open!["merge"]);
    }

    [Fact]
    public async Task UnknownStage_400()
    {
        var resp = await _client.PostAsync("/api/gates/nonsense/hold", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
