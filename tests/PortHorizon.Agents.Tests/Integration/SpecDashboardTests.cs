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
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

/// <summary>
/// P1.5.a dashboard-level test: spin up the same endpoints the real
/// dashboard exposes (SpecEndpoints + DashboardHost wiring), but on an
/// ephemeral port and without all the other dashboard tabs. Exercises
/// the full HTTP round-trip for specs.
/// </summary>
public class SpecDashboardTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public SpecDashboardTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-spec-api-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
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

        SpecEndpoints.MapSpecEndpoints(app, _specs, new SpecExtractionReader(_issues), NullLogger<DashboardHost>.Instance, new Core.IntakeStore(_issues));
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
    public async Task List_Empty_Returns200()
    {
        var resp = await _client.GetAsync("/api/specs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Create_Valid_Returns201()
    {
        var resp = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = "first body", author = "alice" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var spec = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("spec-", spec.GetProperty("id").GetString());
        Assert.Equal("Draft", spec.GetProperty("status").GetString());
        Assert.Equal(1, spec.GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task Create_MissingFields_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/specs", new { title = "t" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_AfterCreate_ReturnsCurrentBody()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = "x" });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.GetAsync($"/api/specs/{id}");
        var fetched = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("x", fetched.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Get_Missing_Returns404()
    {
        var resp = await _client.GetAsync("/api/specs/spec-missing");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_UpdateBody_AppendsNewVersion()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = "v1" });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.PatchAsJsonAsync($"/api/specs/{id}",
            new { op = "update_body", body = "v2", author = "bob" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var updated = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, updated.GetProperty("currentVersion").GetInt32());
        Assert.Equal("v2", updated.GetProperty("body").GetString());

        var versions = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/versions");
        Assert.Equal(2, versions.GetArrayLength());
    }

    [Fact]
    public async Task Patch_SetStatus_ApprovesWithoutNewVersion()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = "x" });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.PatchAsJsonAsync($"/api/specs/{id}",
            new { op = "set_status", status = "Approved" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var updated = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approved", updated.GetProperty("status").GetString());
        Assert.Equal(1, updated.GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task Patch_UnknownOp_Returns400()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = "x" });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;
        var resp = await _client.PatchAsJsonAsync($"/api/specs/{id}",
            new { op = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns204()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = "x" });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.DeleteAsync($"/api/specs/{id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var get = await _client.GetAsync($"/api/specs/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task List_FilterByProjectAndStatus()
    {
        await _client.PostAsJsonAsync("/api/specs", new { projectId = "P1", title = "S1", body = "x" });
        await _client.PostAsJsonAsync("/api/specs", new { projectId = "P1", title = "S2", body = "y" });
        await _client.PostAsJsonAsync("/api/specs", new { projectId = "P2", title = "S3", body = "z" });

        var p1All = await _client.GetFromJsonAsync<JsonElement>("/api/specs?project=P1");
        Assert.Equal(2, p1All.GetArrayLength());

        var p1Draft = await _client.GetFromJsonAsync<JsonElement>("/api/specs?project=P1&status=Draft");
        Assert.Equal(2, p1Draft.GetArrayLength());

        var all = await _client.GetFromJsonAsync<JsonElement>("/api/specs");
        Assert.Equal(3, all.GetArrayLength());
    }

    [Fact]
    public async Task DiagramsEndpoint_ReturnsExtractedDiagrams()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = """
                ## Diagrams
                ```mermaid
                flowchart LR
                  A --> B
                ```
                ```mermaid
                sequenceDiagram
                  A->>B: hi
                ```
                """ });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/diagrams");
        Assert.Equal(2, resp.GetArrayLength());
        Assert.Equal(0, resp[0].GetProperty("ordinal").GetInt32());
        Assert.Equal("flowchart", resp[0].GetProperty("kind").GetString());
        Assert.Equal(1, resp[1].GetProperty("ordinal").GetInt32());
        Assert.Equal("sequencediagram", resp[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TouchesEndpoint_ReturnsExtractedTouches()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = """
                ## Touches
                - PortHorizon.Core.Auth
                - PortHorizon.Dashboard.Theming
                """ });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/touches");
        Assert.Equal(2, resp.GetArrayLength());
        Assert.Contains(resp.EnumerateArray(),
            t => t.GetProperty("moduleId").GetString() == "PortHorizon.Core.Auth");
        Assert.Contains(resp.EnumerateArray(),
            t => t.GetProperty("moduleId").GetString() == "PortHorizon.Dashboard.Theming");
        // The extraction populates source='auto'.
        Assert.All(resp.EnumerateArray(),
            t => Assert.Equal("auto", t.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task DepsEndpoint_ReturnsExtractedDeps()
    {
        var created = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "P", title = "T", body = """
                ## Dependencies
                - blocks spec-portal-redirect
                - depends_on spec-auth-claims
                """ });
        var spec = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var resp = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/deps");
        Assert.Equal(2, resp.GetArrayLength());
        Assert.Contains(resp.EnumerateArray(),
            d => d.GetProperty("kind").GetString() == "blocks"
              && d.GetProperty("toSpecId").GetString() == "spec-portal-redirect");
        Assert.Contains(resp.EnumerateArray(),
            d => d.GetProperty("kind").GetString() == "depends_on"
              && d.GetProperty("toSpecId").GetString() == "spec-auth-claims");
    }

    [Fact]
    public async Task DiagramsEndpoint_MissingSpec_ReturnsEmptyArray()
    {
        var resp = await _client.GetFromJsonAsync<JsonElement>("/api/specs/spec-missing/diagrams");
        Assert.Equal(0, resp.GetArrayLength());
    }

    [Fact]
    public async Task SessionSpecsEndpoint_ReturnsSpecsProducedByIntake()
    {
        // Simulate an intake session with a proposed epic + spec
        // linked back via parent_issue_id. We use IssueStore directly
        // for setup since this is a low-level test.
        var issue = await _issues.CreateAsync(new NewIssue(
            Type: "epic", Title: "Demo epic", Description: "x"));
        var intake = new Core.IntakeStore(_issues);
        var session = await intake.CreateAsync("PortHorizon", "demo", default);
        await intake.AppendMessageAsync(session.Id,
            new NewIntakeMessage(IntakeMessageRole.User, "demo"), default);
        await intake.AppendMessageAsync(session.Id,
            new NewIntakeMessage(IntakeMessageRole.System,
                $"Proposed epic: {issue.Id} - Demo epic",
                ProposedEpicId: issue.Id, ProposedEpicTitle: "Demo epic"), default);

        // Spec whose parent_issue_id = our issue.
        var specStore = new SpecStore(_issues);
        await specStore.CreateAsync(new NewSpec(
            ProjectId: "PortHorizon", Title: "Demo spec",
            Body: "## Touches\n- DemoModule", ParentIssueId: issue.Id));

        var resp = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/intake/sessions/{session.Id}/specs");
        Assert.Equal(1, resp.GetArrayLength());
        Assert.Equal("Demo spec", resp[0].GetProperty("title").GetString());
        Assert.Equal(issue.Id, resp[0].GetProperty("parentIssueId").GetString());
    }

    [Fact]
    public async Task SessionSpecsEndpoint_MissingSession_Returns404()
    {
        var resp = await _client.GetAsync("/api/intake/sessions/intake-missing/specs");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
