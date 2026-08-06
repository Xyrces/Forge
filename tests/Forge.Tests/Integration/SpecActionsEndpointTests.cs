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

public class SpecActionsEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public SpecActionsEndpointTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-spec-actions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        var designArtifacts = new DesignArtifactStore(_dbPath);
        var holder = new SpecStoreHolder();
        _specs = new SpecStore(_issues, designArtifacts: designArtifacts);
        holder.Set(_specs);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        SpecEndpoints.MapSpecEndpoints(app, _specs, new NullSpecExtractionReader(),
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
    public async Task Actions_DraftSpec_ApproveEnabled()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "proj-1", Title: "Test spec", Body: "Body"), default);

        var resp = await _client.GetAsync($"/api/specs/{spec.Id}/actions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ActionsShape>();
        Assert.NotNull(body);
        Assert.True(body!.CanApprove);
        Assert.False(body.CanStartGrooming);
        Assert.False(body.CanShip);
    }

    [Fact]
    public async Task Actions_DesignedSpec_GroomEnabled()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "proj-1", Title: "Test", Body: "Body"), default);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign, default);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Designed, default);

        var resp = await _client.GetAsync($"/api/specs/{spec.Id}/actions");
        var body = await resp.Content.ReadFromJsonAsync<ActionsShape>();
        Assert.False(body!.CanApprove);
        Assert.True(body.CanStartGrooming);
    }

    [Fact]
    public async Task Actions_GroomedSpec_ShipEnabled()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "proj-1", Title: "Test", Body: "Body"), default);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign, default);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Designed, default);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming, default);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Groomed, default);

        var resp = await _client.GetAsync($"/api/specs/{spec.Id}/actions");
        var body = await resp.Content.ReadFromJsonAsync<ActionsShape>();
        Assert.True(body!.CanShip);
    }

    [Fact]
    public async Task Actions_UnknownSpec_NotFound()
    {
        var resp = await _client.GetAsync("/api/specs/nope/actions");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Specs_FilteredByStatus_ReturnsFilteredList()
    {
        var s1 = await _specs.CreateAsync(new NewSpec(ProjectId: "p", Title: "A", Body: "x"), default);
        var s2 = await _specs.CreateAsync(new NewSpec(ProjectId: "p", Title: "B", Body: "y"), default);
        await _specs.SetStatusAsync(s1.Id, SpecStatus.ReadyForDesign, default);
        await _specs.SetStatusAsync(s1.Id, SpecStatus.Designed, default);

        var resp = await _client.GetAsync("/api/specs?status=Designed");
        var list = await resp.Content.ReadFromJsonAsync<List<SpecShape>>();
        Assert.Single(list!);
        Assert.Equal(s1.Id, list![0].Id);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class ActionsShape
    {
        public bool CanApprove { get; set; }
        public bool CanStartGrooming { get; set; }
        public bool CanShip { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class SpecShape
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
