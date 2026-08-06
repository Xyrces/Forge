using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator.Sprint;
using Forge.Projects;
using Forge.Configuration;
using Forge.Tests.Integration;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// GET /api/sprints/building — the inter-sprint build-state snapshot
/// the assembler writes to the project's memory store each tick
/// (operator request 2026-08-06: a completed sprint must not look
/// "stuck" while the next one triages/materializes/grooms).
/// </summary>
public class SprintBuildEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;
    private readonly SpecStore _specs;
    private readonly SprintAssembler _assembler;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public SprintBuildEndpointTests()
    {
        // Work-dir pattern (not a bare file in the temp root): the
        // sqlite -wal/-shm companions must be cleaned too.
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-build-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _sprints = new SprintStore(_issues);
        _specs = new SpecStore(_issues);
        _assembler = new SprintAssembler(
            new ProjectContextFactory(new List<ProjectOptions>()),
            new InMemoryDashboardEventBus(), NullLogger<SprintAssembler>.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        SprintBuildEndpoints.Map(app, _issues,
            new ProjectContextFactory(new List<ProjectOptions>()));
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

    private Task Tick() => _assembler.TickProjectAsync("test", _issues, _sprints, _specs, CancellationToken.None);

    [Fact]
    public async Task UnknownPhase_WhenNoSnapshotYet()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/sprints/building");
        Assert.Equal("unknown", doc.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task UnknownProject_404s()
    {
        var resp = await _client.GetAsync("/api/sprints/building?projectId=nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task IdleSnapshot_AfterTickWithEmptyBacklog()
    {
        await Tick();
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/sprints/building");
        Assert.Equal("idle", doc.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task AwaitingGroomSnapshot_ListsPendingFollowUps()
    {
        var anchor = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "anchor"));
        var fup = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "ungroomed follow-up",
            Metadata: new Dictionary<string, object> { ["followUpOf"] = anchor.Id }));

        await Tick();

        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/sprints/building");
        Assert.Equal("awaiting-groom", doc.GetProperty("phase").GetString());
        var pending = doc.GetProperty("pendingGroom");
        Assert.Equal(1, pending.GetArrayLength());
        Assert.Equal(fup.Id, pending[0].GetProperty("id").GetString());
        Assert.Equal("ungroomed follow-up", pending[0].GetProperty("title").GetString());
    }
}
