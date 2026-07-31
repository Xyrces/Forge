using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests.Integration;

public class TaskEndpointsTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly AgentMessageBus _bus;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public TaskEndpointsTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("task-ep");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _bus = new AgentMessageBus();

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        TaskEndpoints.MapTaskEndpoints(app, _issues, _bus, null, NullLogger<DashboardHost>.Instance);
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
    public async Task InProgress_Empty_ReturnsEmpty()
    {
        var resp = await _client.GetAsync("/api/tasks/in-progress");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<TaskShape>>();
        Assert.Empty(list!);
    }

    [Fact]
    public async Task InProgress_IncludesEvents()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Test", Priority: 2, Assignee: "forge"), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, error: null, default);
        await _issues.AddEventAsync(task.Id, "agent.started", "ready");

        var resp = await _client.GetAsync("/api/tasks/in-progress");
        var list = await resp.Content.ReadFromJsonAsync<List<TaskShape>>();
        Assert.Single(list!);
        Assert.Equal(task.Id, list![0].Id);
        Assert.True(list[0].Events.Count >= 2);
    }

    [Fact]
    public async Task RetryMessage_EnqueuesToBus()
    {
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        var task = (await _issues.ListAsync(new IssueFilter(), default)).First();
        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/retry-message", new { text = "operator override" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, _bus.Count(task.Id));
    }

    [Fact]
    public async Task RetryMessage_UnknownTask_Returns404()
    {
        // The audit found the endpoint returned success for any id.
        var resp = await _client.PostAsJsonAsync("/api/tasks/task-nope/retry-message", new { text = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task RetryMessage_MissingText_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/tasks/T-42/retry-message", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Recover_NoRecoveryService_Returns503()
    {
        var resp = await _client.PostAsync("/api/tasks/T-42/recover", null);
        Assert.Equal(503, (int)resp.StatusCode);
    }

    [Fact]
    public async Task ListEvents_ReturnsChronological()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Test", Priority: 2), default);
        await _issues.AddEventAsync(task.Id, "a", "first");
        await _issues.AddEventAsync(task.Id, "b", "second");

        var list = await _issues.ListEventsAsync(task.Id, 10, default);
        Assert.Equal(3, list.Count);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class TaskShape
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string? DispatchCheckpoint { get; set; }
        public int RecoveryAttempts { get; set; }
        public List<EventShape> Events { get; set; } = new();
    }

    public sealed class EventShape
    {
        public string Kind { get; set; } = "";
        public DateTime At { get; set; }
    }
}
