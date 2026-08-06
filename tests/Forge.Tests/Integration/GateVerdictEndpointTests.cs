using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public class GateVerdictEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _store;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public GateVerdictEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-gv-ep-{Guid.NewGuid():N}.db");
        _store = new IssueStore(_dbPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));

        var app = builder.Build();
        GateVerdictEndpoints.MapGateVerdictEndpoints(app, _store, 
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task ReturnsVerdicts_WithExpectedShape()
    {
        // Arrange: create a task with planGate verdicts
        await _store.CreateAsync(new NewIssue(
            Type: "task",
            Title: "gate test",
            Metadata: new Dictionary<string, object>
            {
                ["planGate"] = """{"approved":true,"verdicts":[{"gate":"plan-schema","outcome":"Approve","feedback":"schema looks good"},{"gate":"plan-llm-review","outcome":"Revise","feedback":"add more detail"}]}"""
            }));

        // Act
        var resp = await _client.GetAsync("/api/gates/verdicts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<VerdictShape[]>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Length);

        var schema = body.Single(v => v.Gate == "plan-schema");
        Assert.Equal("Approve", schema.Outcome);
        Assert.Equal("schema looks good", schema.Feedback);

        var llm = body.Single(v => v.Gate == "plan-llm-review");
        Assert.Equal("Revise", llm.Outcome);
        Assert.Equal("add more detail", llm.Feedback);
    }

    [Fact]
    public async Task LimitParam_Respected()
    {
        await _store.CreateAsync(new NewIssue(
            Type: "task",
            Title: "multi gate",
            Metadata: new Dictionary<string, object>
            {
                ["planGate"] = """{"verdicts":[{"gate":"gate-a","outcome":"Approve","feedback":"a"}]}"""
            }));
        await _store.CreateAsync(new NewIssue(
            Type: "task",
            Title: "multi gate 2",
            Metadata: new Dictionary<string, object>
            {
                ["planGate"] = """{"verdicts":[{"gate":"gate-b","outcome":"Approve","feedback":"b"}]}"""
            }));

        var resp = await _client.GetAsync("/api/gates/verdicts?limit=1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<VerdictShape[]>();
        Assert.NotNull(body);
        Assert.Single(body);
    }

    [Fact]
    public async Task ReturnsEmptyArray_WhenNoVerdicts()
    {
        var resp = await _client.GetAsync("/api/gates/verdicts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<VerdictShape[]>();
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    [Fact]
    public async Task Post_Returns405()
    {
        using var postResp = await _client.PostAsync("/api/gates/verdicts", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postResp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed record VerdictShape(string TaskId, string Gate, string Outcome, string Feedback, DateTime Timestamp);
}
