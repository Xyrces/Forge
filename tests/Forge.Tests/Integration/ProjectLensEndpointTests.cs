using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Projects;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Multi-project lens: UI-facing endpoints must read/write the store
/// OWNED by ?projectId= (or ?project= for specs), never silently the
/// primary project's store. Ids are per-project sequences — a
/// lens-blind read returns the primary's same-numbered row, which is
/// exactly the "everything belongs to the first project" confusion
/// this suite guards against.
/// </summary>
public class ProjectLensEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;        // primary == alpha
    private readonly SprintStore _sprints;
    private readonly SpecStore _specs;
    private readonly ProjectContextFactory _factory;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public ProjectLensEndpointTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-lens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        var alphaDb = Path.Combine(_workDir, "alpha.db");
        var betaDb = Path.Combine(_workDir, "beta.db");

        _issues = new IssueStore(alphaDb);
        _sprints = new SprintStore(_issues);
        _specs = new SpecStore(_issues);
        var agents = new AgentStore(_issues);
        var skills = new SkillStore(_issues);
        var bus = new AgentMessageBus();
        var audit = new SprintProposalAuditStore(alphaDb);
        var propose = new SprintProposeService(_issues, _sprints, new DeterministicScorer(), audit);

        _factory = new ProjectContextFactory(
            new List<ProjectOptions>
            {
                new() { Id = "alpha", Name = "alpha", Root = string.Empty },
                new() { Id = "beta", Name = "beta", Root = string.Empty },
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["alpha"] = alphaDb,
                ["beta"] = betaDb,
            });

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var logger = NullLogger<DashboardHost>.Instance;
        NowEndpoints.MapNowEndpoints(app, _issues, _specs, _sprints, null, null, _factory);
        FlowEndpoints.MapFlowEndpoints(app, _issues, _specs, _sprints, null, null, null, _factory);
        SprintProposeEndpoints.MapSprintProposeEndpoints(app, propose, audit, logger, _factory);
        SpecEndpoints.MapSpecEndpoints(app, _specs, new NullSpecExtractionReader(), logger,
            projectContexts: _factory, issues: _issues);
        DashboardEndpoints.MapP1Endpoints(app, _issues, agents, skills, _sprints, bus, logger, _factory);
        TaskEndpoints.MapTaskEndpoints(app, _issues, bus, null, logger, _factory, _sprints);
        AgentRunEndpoints.MapAgentRunEndpoints(app, new AgentRunStore(alphaDb), _factory);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private Forge.Projects.ProjectContext Beta => _factory.Find("beta")!;

    private async Task<string> CreateBetaTaskAsync(string title)
    {
        var issue = await Beta.Issues.CreateAsync(
            new NewIssue(Type: "task", Title: title, Priority: 2), default);
        return issue.Id;
    }

    [Fact]
    public async Task Now_UnifiedFeedAggregatesProjects_Scoped404s()
    {
        var betaId = await CreateBetaTaskAsync("beta-only work");
        var alphaIssue = await _issues.CreateAsync(
            new NewIssue(Type: "task", Title: "alpha work", Priority: 2), default);
        var sprint = await Beta.Sprints.CreateAsync(
            new NewSprint("beta sprint", "g", DateTime.UtcNow, DateTime.UtcNow.AddDays(7)), default);
        await Beta.Sprints.AddIssueAsync(sprint.Id, betaId, default);

        // UNIFIED (no lens): both projects' items, each tagged with
        // its owning project.
        var unified = await _client.GetFromJsonAsync<JsonElement>("/api/now");
        var waiting = unified.GetProperty("waiting").EnumerateArray().ToList();
        Assert.Contains(waiting, w => w.GetProperty("issueId").GetString() == betaId
            && w.GetProperty("projectId").GetString() == "beta");
        Assert.Contains(waiting, w => w.GetProperty("issueId").GetString() == alphaIssue.Id
            && w.GetProperty("projectId").GetString() == "alpha");

        // Sprint chips are per project.
        Assert.Contains(unified.GetProperty("pulse").GetProperty("sprints").EnumerateArray(),
            s => s.GetProperty("projectId").GetString() == "beta"
                && s.GetProperty("id").GetString() == sprint.Id);

        // Cross-project attention links carry the owning project.
        // (Waiting items use the same convention via the UI; attention
        // link shape is server-owned.)
        var scoped = await _client.GetFromJsonAsync<JsonElement>("/api/now?projectId=beta");
        // Ids collide across projects (both mint task-1) — assert on
        // the TITLE, which is unique per side.
        Assert.DoesNotContain(scoped.GetProperty("waiting").EnumerateArray(),
            w => w.GetProperty("title").GetString() == "alpha work");
        Assert.Contains(scoped.GetProperty("waiting").EnumerateArray(),
            w => w.GetProperty("title").GetString() == "beta-only work");

        var resp = await _client.GetAsync("/api/now?projectId=nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AgentRuns_LensReadsOwningProject()
    {
        // A run recorded only in beta's store (per-project writers:
        // the runner resolves the store from the dispatch project).
        var betaRuns = new AgentRunStore(((IssueStore)Beta.Issues).Db);
        await betaRuns.StartAsync("runb1", "task-1", "CoreDev", "kimi/k3", default, projectId: "beta");

        var beta = await _client.GetFromJsonAsync<JsonElement>("/api/agent-runs?projectId=beta");
        Assert.Contains(beta.GetProperty("active").EnumerateArray(),
            r => r.GetProperty("id").GetString() == "runb1");

        var primary = await _client.GetFromJsonAsync<JsonElement>("/api/agent-runs");
        Assert.DoesNotContain(primary.GetProperty("active").EnumerateArray(),
            r => r.GetProperty("id").GetString() == "runb1");

        var detail = await _client.GetAsync("/api/agent-runs/runb1?projectId=beta");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var blind = await _client.GetAsync("/api/agent-runs/runb1");
        Assert.Equal(HttpStatusCode.NotFound, blind.StatusCode);
        var unknown = await _client.GetAsync("/api/agent-runs?projectId=nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Flow_LensReadsOwningProject_JourneyFollowsLens()
    {
        var id = await CreateBetaTaskAsync("beta flow item");

        var beta = await _client.GetFromJsonAsync<JsonElement>("/api/flow?projectId=beta");
        Assert.Contains(beta.GetProperty("allIssues").EnumerateArray(),
            i => i.GetProperty("id").GetString() == id);

        var primary = await _client.GetFromJsonAsync<JsonElement>("/api/flow");
        Assert.DoesNotContain(primary.GetProperty("allIssues").EnumerateArray(),
            i => i.GetProperty("id").GetString() == id);

        // The id exists ONLY in beta — a lens-blind journey lookup 404s
        // instead of returning the primary's same-numbered row.
        var journey = await _client.GetAsync($"/api/flow/issues/{id}?projectId=beta");
        Assert.Equal(HttpStatusCode.OK, journey.StatusCode);
        var blind = await _client.GetAsync($"/api/flow/issues/{id}");
        Assert.Equal(HttpStatusCode.NotFound, blind.StatusCode);
    }

    [Fact]
    public async Task SprintPropose_Commit_WritesOwningProject()
    {
        var id = await CreateBetaTaskAsync("beta candidate");

        var propose = await _client.GetFromJsonAsync<JsonElement>(
            "/api/sprints/propose-next?projectId=beta&count=5");
        Assert.Contains(propose.GetProperty("candidates").EnumerateArray(),
            c => c.GetProperty("taskId").GetString() == id);

        var commit = await _client.PostAsJsonAsync("/api/sprints/propose-next/commit", new
        {
            auditId = propose.GetProperty("auditId").GetInt64(),
            taskIds = new[] { id },
            theme = "beta sprint",
            projectId = "beta",
        });
        commit.EnsureSuccessStatusCode();
        var body = await commit.Content.ReadFromJsonAsync<JsonElement>();
        var sprintId = body.GetProperty("sprintId").GetString()!;

        // The sprint physically lives in beta's store — never the primary's.
        Assert.Contains((await Beta.Sprints.ListAsync(activeOnly: false, default)),
            s => s.Id == sprintId);
        Assert.DoesNotContain((await _sprints.ListAsync(activeOnly: false, default)),
            s => s.Id == sprintId);
        Assert.Contains(id, await Beta.Sprints.GetIssueIdsAsync(sprintId, default));
    }

    [Fact]
    public async Task Spec_CreateAndRead_FollowOwningProject()
    {
        var create = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "beta", title = "beta spec", body = "b" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var specId = created.GetProperty("id").GetString()!;

        Assert.NotNull(await Beta.Specs.GetAsync(specId, default));
        Assert.Empty(await _specs.ListAsync(projectId: null, status: null, default));

        var withLens = await _client.GetAsync($"/api/specs/{specId}?project=beta");
        Assert.Equal(HttpStatusCode.OK, withLens.StatusCode);
        var blind = await _client.GetAsync($"/api/specs/{specId}");
        Assert.Equal(HttpStatusCode.NotFound, blind.StatusCode);

        var unknown = await _client.PostAsJsonAsync("/api/specs",
            new { projectId = "nope", title = "x", body = "b" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task SprintMutations_LensWritesOwningProject()
    {
        var create = await _client.PostAsJsonAsync("/api/sprints?projectId=beta",
            new { name = "beta sprint", goal = "g", startDate = DateTime.UtcNow, endDate = DateTime.UtcNow.AddDays(7) });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var sprintId = created.GetProperty("id").GetString()!;

        Assert.Contains((await Beta.Sprints.ListAsync(activeOnly: false, default)), s => s.Id == sprintId);
        Assert.DoesNotContain((await _sprints.ListAsync(activeOnly: false, default)), s => s.Id == sprintId);
    }

    [Fact]
    public async Task Deps_LensReadsAndWritesOwningProject()
    {
        var blocker = await CreateBetaTaskAsync("beta blocker");
        var blocked = await CreateBetaTaskAsync("beta blocked");

        var add = await _client.PostAsJsonAsync($"/api/state/issues/{blocked}/deps?projectId=beta",
            new { blockerId = blocker, kind = "blocks" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var withLens = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/state/issues/{blocked}/deps?projectId=beta");
        Assert.Single(withLens.GetProperty("edges").EnumerateArray());

        // Lens-blind: the id doesn't exist in the primary store at all.
        var blind = await _client.GetAsync($"/api/state/issues/{blocked}/deps");
        Assert.Equal(HttpStatusCode.NotFound, blind.StatusCode);
    }

    [Fact]
    public async Task RetryMessage_ExistenceCheckFollowsLens()
    {
        var id = await CreateBetaTaskAsync("beta retry target");

        var blind = await _client.PostAsJsonAsync($"/api/tasks/{id}/retry-message", new { text = "go" });
        Assert.Equal(HttpStatusCode.NotFound, blind.StatusCode);

        var withLens = await _client.PostAsJsonAsync($"/api/tasks/{id}/retry-message?projectId=beta",
            new { text = "go" });
        Assert.Equal(HttpStatusCode.OK, withLens.StatusCode);
    }

    [Fact]
    public async Task UnknownProject_WriteRoutes_404InsteadOfPrimaryFallback()
    {
        // A typo'd lens must NEVER silently read/write the primary
        // project's same-numbered rows.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/sprints?projectId=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync("/api/sprints?projectId=nope",
                new { name = "x", goal = "g", startDate = DateTime.UtcNow, endDate = DateTime.UtcNow })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync("/api/state/issues/task-1/deps?projectId=nope",
                new { blockerId = "task-2", kind = "blocks" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/state/issues/task-1/deps?projectId=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/sprints/propose-next?projectId=nope")).StatusCode);

        // Nothing leaked into the primary store.
        Assert.Empty(await _sprints.ListAsync(activeOnly: false, default));
    }

    [Fact]
    public async Task Commit_ForeignTaskIds_400_MissingAudit_404()
    {
        var betaTask = await CreateBetaTaskAsync("beta commit target");
        // Ids are per-project sequences: alpha's task-1 collides with
        // beta's task-1, so a genuinely "foreign" id needs alpha's
        // sequence to run AHEAD of beta's.
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "alpha-1", Priority: 2), default);
        var alphaOnly = await _issues.CreateAsync(
            new NewIssue(Type: "task", Title: "alpha-only", Priority: 2), default);
        Assert.NotEqual(betaTask, alphaOnly.Id);

        // A task id that exists only in the PRIMARY store must not be
        // attachable to a beta sprint.
        var foreign = await _client.PostAsJsonAsync("/api/sprints/propose-next/commit", new
        {
            auditId = 1L,
            taskIds = new[] { betaTask, alphaOnly.Id },
            projectId = "beta",
        });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // No audit row with this id in beta's store → clean 404, not a
        // 500 (and never a silent commit of the primary store's
        // same-numbered audit row).
        var missingAudit = await _client.PostAsJsonAsync("/api/sprints/propose-next/commit", new
        {
            auditId = 99999L,
            taskIds = new[] { betaTask },
            projectId = "beta",
        });
        Assert.Equal(HttpStatusCode.NotFound, missingAudit.StatusCode);

        Assert.Empty(await Beta.Sprints.ListAsync(activeOnly: false, default));
    }

    private static int GetEphemeralPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
