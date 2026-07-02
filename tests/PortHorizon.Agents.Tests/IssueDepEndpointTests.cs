using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Orchestrator;
using PortHorizon.Agents.Tests.Integration;
using Xunit;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// Phase 2: HTTP endpoints for the issue dependency graph.
/// Uses real stores (IssueStore/AgentStore/SkillStore/SprintStore) so we
/// don't have to keep our no-op stubs in sync with the interface.
/// </summary>
public class IssueDepEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public IssueDepEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-dep-api-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        var agents = new AgentStore(_issues);
        var skills = new SkillStore(_issues);
        var sprints = new SprintStore(_issues);
        var bus = new AgentMessageBus();

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        DashboardEndpoints.MapP1Endpoints(app, _issues, agents, skills, sprints, bus, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static int GetEphemeralPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<string> CreateIssueAsync(string title = "t")
    {
        var resp = await _client.PostAsJsonAsync("/api/state/issues",
            new { type = "task", title });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task GetDeps_NoEdges_ReturnsBlockedFalse()
    {
        var id = await CreateIssueAsync();
        var resp = await _client.GetFromJsonAsync<JsonElement>($"/api/state/issues/{id}/deps");
        Assert.False(resp.GetProperty("blocked").GetBoolean());
        Assert.Equal(0, resp.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task GetDeps_MissingIssue_Returns404()
    {
        var resp = await _client.GetAsync("/api/state/issues/task-missing/deps");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostDep_Valid_Returns201()
    {
        var blocker = await CreateIssueAsync("blocker");
        var blocked = await CreateIssueAsync("blocked");

        var resp = await _client.PostAsJsonAsync(
            $"/api/state/issues/{blocked}/deps",
            new { blockerId = blocker, kind = "blocks" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(blocker, body.GetProperty("blockerId").GetString());
        Assert.Equal(blocked, body.GetProperty("blockedId").GetString());
        Assert.Equal("blocks", body.GetProperty("kind").GetString());

        var get = await _client.GetFromJsonAsync<JsonElement>($"/api/state/issues/{blocked}/deps");
        Assert.True(get.GetProperty("blocked").GetBoolean());
        Assert.Equal(1, get.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task PostDep_MissingBlocker_Returns404()
    {
        var blocked = await CreateIssueAsync("blocked");
        var resp = await _client.PostAsJsonAsync(
            $"/api/state/issues/{blocked}/deps",
            new { blockerId = "task-nope", kind = "blocks" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostDep_MissingBlocked_Returns404()
    {
        var blocker = await CreateIssueAsync("blocker");
        var resp = await _client.PostAsJsonAsync(
            "/api/state/issues/task-nope/deps",
            new { blockerId = blocker, kind = "blocks" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostDep_SelfLoop_Returns400()
    {
        var a = await CreateIssueAsync("A");
        var resp = await _client.PostAsJsonAsync(
            $"/api/state/issues/{a}/deps",
            new { blockerId = a, kind = "blocks" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostDep_UnknownKind_Returns400()
    {
        var blocker = await CreateIssueAsync();
        var blocked = await CreateIssueAsync();
        var resp = await _client.PostAsJsonAsync(
            $"/api/state/issues/{blocked}/deps",
            new { blockerId = blocker, kind = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteDep_Existing_Returns204()
    {
        var blocker = await CreateIssueAsync();
        var blocked = await CreateIssueAsync();
        await _client.PostAsJsonAsync($"/api/state/issues/{blocked}/deps",
            new { blockerId = blocker, kind = "blocks" });

        var del = await _client.DeleteAsync($"/api/state/issues/{blocked}/deps/{blocker}/blocks");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await _client.GetFromJsonAsync<JsonElement>($"/api/state/issues/{blocked}/deps");
        Assert.False(get.GetProperty("blocked").GetBoolean());
        Assert.Equal(0, get.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task DeleteDep_Missing_Returns404()
    {
        var blocked = await CreateIssueAsync();
        var del = await _client.DeleteAsync($"/api/state/issues/{blocked}/deps/task-nope/blocks");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }
}