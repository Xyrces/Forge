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
/// The vision editor save path: PUT /api/vision writes the file
/// (creating it when missing — previously the file had to
/// "magically exist") and refreshes the vision/master memory key
/// so subsequent agent runs see the new content.
/// </summary>
public class VisionEndpointWriteTests : IAsyncLifetime
{
    private readonly string _workDir;
    private VisionStore _vision = null!;
    private MemoryStore _memory = null!;
    private IHost _host = null!;
    private HttpClient _client = null!;

    public VisionEndpointWriteTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("visionw");
        Directory.CreateDirectory(_workDir);
    }

    public async Task InitializeAsync()
    {
        // Deliberately no docs/ dir: the file starts missing.
        _vision = new VisionStore(_workDir, "docs/MASTER_DESIGN.md");
        _vision.Reload();
        // The memory table is part of the IssueStore schema; bootstrap
        // it the same way Program.cs does (IssueStore over memory.db).
        var bootstrap = new IssueStore(Path.Combine(_workDir, "memory.db"));
        await bootstrap.DisposeAsync();
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        VisionEndpoints.MapVisionEndpoints(app, _vision, NullLogger<DashboardHost>.Instance, _memory);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
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
    public async Task Put_CreatesMissingFile_AndRefreshesMemory()
    {
        Assert.False(_vision.Get().Exists);

        var resp = await _client.PutAsJsonAsync("/api/vision", new { content = "# Vision\n\nBuild the thing." });
        resp.EnsureSuccessStatusCode();

        var snap = _vision.Get();
        Assert.True(snap.Exists);
        Assert.Equal("# Vision\n\nBuild the thing.", snap.Content);
        Assert.True(File.Exists(Path.Combine(_workDir, "docs", "MASTER_DESIGN.md")));

        var mem = await _memory.RecallAsync("vision/master");
        Assert.Single(mem);
        Assert.Equal(snap.Content, mem[0].Body);
    }

    [Fact]
    public async Task Put_OverwritesExisting_AndMemoryTracksLatest()
    {
        await _client.PutAsJsonAsync("/api/vision", new { content = "v1" });
        await _client.PutAsJsonAsync("/api/vision", new { content = "v2" });

        var snap = _vision.Get();
        Assert.Equal("v2", snap.Content);
        var mem = await _memory.RecallAsync("vision/master");
        Assert.Single(mem);
        Assert.Equal("v2", mem[0].Body);
    }
}
