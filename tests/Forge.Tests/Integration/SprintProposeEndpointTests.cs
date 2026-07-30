using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

public class SprintProposeEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;
    private readonly SprintProposalAuditStore _audit;
    private readonly SprintProposeService _service;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public SprintProposeEndpointTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("sprint-propose");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _sprints = new SprintStore(_issues);
        _audit = new SprintProposalAuditStore(_dbPath);
        _service = new SprintProposeService(_issues, _sprints, new DeterministicScorer(), _audit);

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        SprintProposeEndpoints.MapSprintProposeEndpoints(app, _service, _audit,
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
    public async Task Propose_EmptyBacklog_ReturnsEmptyCandidates()
    {
        var resp = await _client.GetAsync("/api/sprints/propose-next");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ProposeShape>();
        Assert.NotNull(body);
        Assert.Empty(body!.Candidates);
        Assert.Empty(body.SelectedTaskIds);
        Assert.True(body.AuditId > 0);
    }

    [Fact]
    public async Task Propose_WithTasks_ReturnsRankedCandidates()
    {
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Memory bank hot path", Priority: 1), default);
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Refactor dashboard", Priority: 3), default);

        var resp = await _client.GetAsync("/api/sprints/propose-next?count=5");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ProposeShape>();
        Assert.Equal(2, body!.Candidates.Count);
        Assert.True(body.Candidates[0].Score >= body.Candidates[1].Score);
    }

    [Fact]
    public async Task Propose_ThemeMatch_BumpsScore()
    {
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "Memory bank refinements", Priority: 2), default);

        var respWith = await _client.GetAsync("/api/sprints/propose-next?theme=memory&count=5");
        var bodyWith = await respWith.Content.ReadFromJsonAsync<ProposeShape>();

        var respWithout = await _client.GetAsync("/api/sprints/propose-next?count=5");
        var bodyWithout = await respWithout.Content.ReadFromJsonAsync<ProposeShape>();

        Assert.True(bodyWith!.Candidates[0].Score > bodyWithout!.Candidates[0].Score);
    }

    [Fact]
    public async Task Commit_CreatesSprintAndLinksTasks()
    {
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "P6 UI", Priority: 2), default);
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "P6 backend", Priority: 2), default);

        var proposeResp = await _client.GetAsync("/api/sprints/propose-next?count=2");
        var propose = await proposeResp.Content.ReadFromJsonAsync<ProposeShape>();

        var commit = new
        {
            auditId = propose!.AuditId,
            taskIds = propose.SelectedTaskIds,
            theme = "P6",
            goal = "Ship the new UI",
            committedBy = "test",
        };
        var resp = await _client.PostAsJsonAsync("/api/sprints/propose-next/commit", commit);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CommitShape>();
        Assert.NotNull(body!.SprintId);
        Assert.Equal(propose.AuditId, body.AuditId);

        var sprints = await _sprints.ListAsync(activeOnly: false, default);
        var newSprint = sprints.First(s => s.Id == body.SprintId);
        Assert.Equal(SprintStatus.Active, newSprint.Status);

        var ids = await _sprints.GetIssueIdsAsync(body.SprintId, default);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task ScoringAudit_ReturnsRowsNewestFirst()
    {
        await _client.GetAsync("/api/sprints/propose-next?count=2");
        await _client.GetAsync("/api/sprints/propose-next?count=2");

        var resp = await _client.GetAsync("/api/sprints/scoring-audit?limit=10");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rows = await resp.Content.ReadFromJsonAsync<List<AuditShape>>();
        Assert.Equal(2, rows!.Count);
        Assert.True(rows[0].Timestamp >= rows[1].Timestamp);
    }

    [Fact]
    public async Task Commit_MissingAuditId_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/api/sprints/propose-next/commit",
            new { taskIds = new[] { "T-1" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public sealed class ProposeShape
    {
        public long AuditId { get; set; }
        public string? Theme { get; set; }
        public List<CandidateShape> Candidates { get; set; } = new();
        public List<string> SelectedTaskIds { get; set; } = new();
        public Dictionary<string, object> Weights { get; set; } = new();
    }

    public sealed class CandidateShape
    {
        public string TaskId { get; set; } = "";
        public string Title { get; set; } = "";
        public int Score { get; set; }
        public List<string> Breakdown { get; set; } = new();
    }

    public sealed class CommitShape
    {
        public long AuditId { get; set; }
        public string SprintId { get; set; } = "";
    }

    public sealed class AuditShape
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string? Theme { get; set; }
    }
}
