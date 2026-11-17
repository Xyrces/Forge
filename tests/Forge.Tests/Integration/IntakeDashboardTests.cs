using Forge.Core.Initiatives;
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
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// P1.4 dashboard-level test: spin up the same endpoints the real
/// dashboard exposes, but on an ephemeral port and without all the
/// other dashboard tabs. Exercises the full HTTP round-trip
/// (POST /sessions -> POST /messages -> POST /accept-epic).
/// </summary>
public class IntakeDashboardTests : IDisposable
{
    private readonly InitiativeApprovalService _initiatives;
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
        _dbPath = TempRoot.Instance.NewDbPath("intake-api");
        _issues = new IssueStore(_dbPath,"P");
        _sprints = new SprintStore(_issues);
        _intake = new IntakeStore(_issues);
        _events = new InMemoryDashboardEventBus();
        _initiatives = new(new InitiativeProposalStore(_issues),_=>new InitiativeIngestionStore(_issues),_events);

        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi.")));
        _intakeRegistry = new IntakeAgentRegistry(projectId =>
            new IntakeAgent(
                projectId, _intake, _issues, _sprints,
                new StubFactory(scripted),
                new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
                new RoleAgentRegistry(), _events, NullLogger<IntakeAgent>.Instance, initiatives:_initiatives));

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
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(_dbPath) ?? Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        // Mount the same endpoints the real dashboard does.
        IntakeEndpoints.MapIntakeEndpoints(app, _intakeRegistry, _issues, _sprints, _intake,
            NullLogger<DashboardHost>.Instance,_initiatives);

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
    public async Task LegacyAccept_RequiresDurableRevisionReview()
    {
        var created = await _client.PostAsJsonAsync("/api/intake/sessions",
            new { projectId = "P", title = "t" });
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetString()!;
        await _client.PostAsJsonAsync($"/api/intake/sessions/{id}/messages", new { text = "hi" });

        var refreshed = await _client.GetFromJsonAsync<JsonElement>($"/api/intake/sessions/{id}");
        var assistantMsgId = refreshed.GetProperty("messages")[1].GetProperty("id").GetInt64();

        var resp = await _client.PostAsync($"/api/intake/sessions/{id}/accept-epic/{assistantMsgId}", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task ProposalListIsBoundedSummaryAndExpandCreatesNoWork()
    {
        for(var i=0;i<23;i++) await _initiatives.Proposals.CreateAsync(new("P",$"finding{i}","forge",$"Suggestion{i}","private full body"));
        var page=await _client.GetFromJsonAsync<JsonElement>("/api/intake/proposals?projectId=P");
        Assert.Equal(20,page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("items")[0].TryGetProperty("body",out _));
        var id=page.GetProperty("items")[0].GetProperty("id").GetString();
        var cursor=page.GetProperty("nextCursor").GetString();
        var next=await _client.GetFromJsonAsync<JsonElement>($"/api/intake/proposals?projectId=P&cursor={cursor}");
        Assert.Equal(3,next.GetProperty("items").GetArrayLength());
        var one=await _client.PostAsync($"/api/intake/proposals/{id}/expand?projectId=P",null);
        var two=await _client.PostAsync($"/api/intake/proposals/{id}/expand?projectId=P",null);
        Assert.Equal(await one.Content.ReadAsStringAsync(),await two.Content.ReadAsStringAsync());
        Assert.Single(await _intake.ListAsync());
        Assert.Empty(await _issues.ListAsync(new IssueFilter()));
        Assert.Empty(await new SpecStore(_issues).ListAsync(null,null));
        Assert.Empty(await _sprints.ListAsync(false));
        Assert.Equal(HttpStatusCode.NotFound,(await _client.GetAsync($"/api/intake/proposals/{id}?projectId=other")).StatusCode);
    }

    [Fact]
    public async Task ApprovalRejectsStaleRevisionAndRepeatedCurrentRequestAdoptsSameOutcome()
    {
        var p=await _initiatives.Proposals.CreateAsync(new("P","source","forge","old","scope"));
        await _client.PostAsync($"/api/intake/proposals/{p.Id}/expand?projectId=P",null);
        var revised=await _initiatives.Proposals.ReviseAsync("P",p.Id,1,"new","new scope",2);
        var stale=await _client.PostAsJsonAsync($"/api/intake/proposals/{p.Id}/approve?projectId=P",new {revision=1,hash=p.Hash});
        Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        Assert.Empty(await _issues.ListAsync(new IssueFilter()));
        var first=await _client.PostAsJsonAsync($"/api/intake/proposals/{p.Id}/approve?projectId=P",new {revised.Revision,revised.Hash});
        var second=await _client.PostAsJsonAsync($"/api/intake/proposals/{p.Id}/approve?projectId=P",new {revised.Revision,revised.Hash});
        Assert.Equal(HttpStatusCode.OK,first.StatusCode);Assert.Equal(HttpStatusCode.OK,second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(),await second.Content.ReadAsStringAsync());
        Assert.Single(await _issues.ListAsync(new IssueFilter()));
        Assert.Single(await new SpecStore(_issues).ListAsync("P",null));
        Assert.Empty(await _sprints.ListAsync(false));
    }

    [Fact]
    public async Task LegacyDraftImportRequiresSeparateRevisionApproval()
    {
        var session=await _intake.CreateAsync("P","legacy");
        var msg=await _intake.AppendMessageAsync(session.Id,new(IntakeMessageRole.System,"draft",ProposedEpicTitle:"Legacy",ProposedEpicDescription:"scope"));
        var old=await _client.PostAsync($"/api/intake/sessions/{session.Id}/accept-epic/{msg.Id}",null);
        Assert.Equal(HttpStatusCode.Conflict,old.StatusCode);
        var imported=await _client.PostAsync($"/api/intake/sessions/{session.Id}/review-draft/{msg.Id}",null);
        Assert.Equal(HttpStatusCode.OK,imported.StatusCode);
        var p=await imported.Content.ReadFromJsonAsync<InitiativeProposal>();
        Assert.NotNull(p);Assert.Null(p.ApprovedAt);Assert.Equal(session.Id,p.SessionId);
        Assert.Empty(await _issues.ListAsync(new IssueFilter()));
    }

    private sealed class StubFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public StubFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
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



