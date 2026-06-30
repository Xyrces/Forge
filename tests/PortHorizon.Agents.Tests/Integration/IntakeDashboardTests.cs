using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// P1.4 dashboard-level test: spin up the same endpoints the real
/// dashboard exposes, but on an ephemeral port and without all the
/// other dashboard tabs. Exercises the full HTTP round-trip
/// (POST /sessions -> POST /messages -> POST /accept-epic).
/// </summary>
public class IntakeDashboardTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;
    private readonly IntakeStore _intake;
    private readonly InMemoryDashboardEventBus _events;
    private readonly IntakeAgentRegistry _intakeRegistry;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public IntakeDashboardTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-intake-api-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _sprints = new SprintStore(_issues);
        _intake = new IntakeStore(_issues);
        _events = new InMemoryDashboardEventBus();

        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi.")));
        _intakeRegistry = new IntakeAgentRegistry(projectId =>
            new IntakeAgent(
                projectId, _intake, _issues, _sprints,
                new StubFactory(scripted),
                new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
                new RoleAgentRegistry(), _events, NullLogger<IntakeAgent>.Instance));

        _host = BuildHost();
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private IHost BuildHost()
    {
        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        // Mount the same endpoints the real dashboard does.
        IntakeEndpoints.MapIntakeEndpoints(app, _intakeRegistry, _issues, _sprints, _intake,
            NullLogger<DashboardHost>.Instance);

        return app;
    }

    private static int GetEphemeralPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task GetSessions_EmptyList_Returns200()
    {
        var resp = await _client.GetAsync("/api/intake/sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sessions = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, sessions.GetArrayLength());
    }

    [Fact]
    public async Task CreateSession_ProjectIdMissing_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/intake/sessions", new { title = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateSession_HappyPath_Returns201WithSession()
    {
        var resp = await _client.PostAsJsonAsync("/api/intake/sessions",
            new { projectId = "PortHorizon", title = "First session" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var session = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("intake-", session.GetProperty("id").GetString());
        Assert.Equal("PortHorizon", session.GetProperty("projectId").GetString());
        Assert.Equal("First session", session.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetSession_AfterCreate_ReturnsMessages()
    {
        var created = await _client.PostAsJsonAsync("/api/intake/sessions",
            new { projectId = "P", title = "t" });
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetString()!;

        var resp = await _client.GetAsync($"/api/intake/sessions/{id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var fetched = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, fetched.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetSession_Missing_Returns404()
    {
        var resp = await _client.GetAsync("/api/intake/sessions/intake-missing");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SendMessage_EmptyText_Returns400()
    {
        var created = await _client.PostAsJsonAsync("/api/intake/sessions",
            new { projectId = "P", title = "t" });
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetString()!;

        var resp = await _client.PostAsJsonAsync($"/api/intake/sessions/{id}/messages", new { text = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SendMessage_HappyPath_AppendsUserAndAssistant()
    {
        var created = await _client.PostAsJsonAsync("/api/intake/sessions",
            new { projectId = "P", title = "t" });
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetString()!;

        var resp = await _client.PostAsJsonAsync($"/api/intake/sessions/{id}/messages",
            new { text = "hello" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var updated = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var messages = updated.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("User", messages[0].GetProperty("role").GetString());
        Assert.Equal("hello", messages[0].GetProperty("content").GetString());
        Assert.Equal("Assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hi.", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AcceptEpic_NotAProposal_Returns400()
    {
        var created = await _client.PostAsJsonAsync("/api/intake/sessions",
            new { projectId = "P", title = "t" });
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetString()!;
        await _client.PostAsJsonAsync($"/api/intake/sessions/{id}/messages", new { text = "hi" });

        var refreshed = await _client.GetFromJsonAsync<JsonElement>($"/api/intake/sessions/{id}");
        var assistantMsgId = refreshed.GetProperty("messages")[1].GetProperty("id").GetInt64();

        var resp = await _client.PostAsync($"/api/intake/sessions/{id}/accept-epic/{assistantMsgId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class StubFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public StubFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }
}

internal static class HostExtensions
{
    public static int GetPort(this IHost host)
    {
        var addresses = host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        if (addresses is null) return 0;
        var first = addresses.FirstOrDefault() ?? "";
        // Format: http://127.0.0.1:NNNN
        var idx = first.LastIndexOf(':');
        return idx >= 0 && int.TryParse(first[(idx + 1)..], out var p) ? p : 0;
    }
}
