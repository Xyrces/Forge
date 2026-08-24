using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
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

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        TaskEndpoints.MapTaskEndpoints(app, _issues, _bus, null, NullLogger<DashboardHost>.Instance,
            lifecycle: new TaskStateMachine(writeAuthority: true, NullLogger<TaskStateMachine>.Instance),
            gitHubForProject: _ => new CloseCapturingGitHub());
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
    public async Task Requeue_WithOpenPr_RestartsStaleWindow()
    {
        // Regression (observed live 2026-07-30, task-12): a requeued
        // task with an hours-old PR tripped the watcher's pr-stale
        // guard (anchored to prOpenedAt) minutes after the requeue.
        // The requeue is explicit progress intent — it must restart
        // the stale window.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom",
            new Dictionary<string, object>
            {
                ["prNumber"] = "123",
                ["prOpenedAt"] = DateTime.UtcNow.AddDays(-3).ToString("O"),
                ["state"] = "Failed",
            }, default);

        var before = DateTime.UtcNow.AddSeconds(-5);
        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/requeue", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Equal("Pending", after.GetMetadata("state"));
        var anchor = DateTimeOffset.Parse(after.GetMetadata("prOpenedAt")!).UtcDateTime;
        Assert.True(anchor >= before, $"prOpenedAt should be refreshed to ~now, got {anchor:O}");
    }

    [Fact]
    public async Task Requeue_WithoutPr_LeavesPrOpenedAtUnset()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom", default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/requeue", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Null(after.GetMetadata("prOpenedAt"));
    }

    [Fact]
    public async Task Requeue_QaUnavailableBlock_ResetsTheQaBudget()
    {
        // 2026-08-24 task-740 loop: a qa-unavailable park burned the
        // per-head QA attempt budget, and the operator requeue cleared
        // strikes but NOT the QA keys — the requeued task instantly
        // re-blocked at the same head. Operator intervention must
        // restart the QA budget exactly like the strike budgets.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "qa unavailable",
            new Dictionary<string, object>
            {
                ["prNumber"] = "123",
                ["blockedKind"] = "qa-unavailable",
                ["qaAttempts"] = "2",
                ["qaAttemptSha"] = "abc123",
                ["qaStartedAt"] = DateTime.UtcNow.AddHours(-1).ToString("O"),
                ["state"] = "BlockedOperator",
            }, default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/requeue", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        foreach (var key in new[] { "qaAttempts", "qaAttemptSha", "qaStartedAt", "blockedKind" })
        {
            Assert.Null(after.GetMetadata(key));
        }
    }

    [Fact]
    public async Task Requeue_OtherBlockKind_KeepsBlockedKind()
    {
        // Only the qa-unavailable marker clears — reviewer-unavailable
        // has its own auto-resume bookkeeping the requeue must not
        // disturb.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "reviewer cooling",
            new Dictionary<string, object>
            {
                ["blockedKind"] = "reviewer-unavailable",
                ["state"] = "BlockedOperator",
            }, default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/requeue", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Equal("reviewer-unavailable", after.GetMetadata("blockedKind"));
    }

    [Fact]
    public async Task ResetStrikes_ClearsAllCounters_AndRequeues()
    {
        // Operator recovery nudge (2026-07-31): every strike counter
        // cleared, verdict de-armed (fresh review, no instant
        // re-trip), stale window restarted, Blocked → Pending.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "circuit breaker",
            new Dictionary<string, object>
            {
                ["prNumber"] = "123",
                ["prOpenedAt"] = DateTime.UtcNow.AddDays(-3).ToString("O"),
                ["reworkAttempts"] = "3",
                ["reworkForSha"] = "abc",
                ["noProgressAttempts"] = "2",
                ["autoResumeAttempts"] = "3",
                ["reviewRound"] = "3",
                ["reviewVerdict"] = "RequestChanges",
                ["reviewSha"] = "abc",
                ["blockedKind"] = "reviewer-unavailable",
                ["state"] = "BlockedOperator",
            }, default);

        var before = DateTime.UtcNow.AddSeconds(-5);
        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/reset-strikes", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        // The lifecycle state must be reset too — otherwise the next
        // dispatch violates (BlockedOperator+Dispatched illegal) and
        // the board shows a live run inside a "blocked" card
        // (observed live 2026-08-01: task-18).
        Assert.Equal("Pending", after.GetMetadata("state"));
        Assert.Equal("1", after.GetMetadata("strikeResetCount"));
        foreach (var key in new[] { "reworkAttempts", "reworkForSha", "noProgressAttempts", "autoResumeAttempts",
                     "reviewRound", "reviewVerdict", "reviewSha", "blockedKind" })
        {
            Assert.Null(after.GetMetadata(key));
        }
        Assert.True(DateTimeOffset.Parse(after.GetMetadata("prOpenedAt")!).UtcDateTime >= before);
        Assert.NotNull(after.GetMetadata("requeuedFromFailedAt"));
    }

    [Fact]
    public async Task ResetStrikes_InProgress_StaysInProgress()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object> { ["reworkAttempts"] = "2" }, default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/reset-strikes", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.InProgress, after.Status);
        Assert.Null(after.GetMetadata("reworkAttempts"));
    }

    [Fact]
    public async Task ResetStrikes_PendingTask_Conflicts()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/reset-strikes", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Park_InProgress_BlocksWithReason()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/park", new { reason = "missing QA stage" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Blocked, after.Status);
        Assert.Equal("operator-park", after.GetMetadata("blockedKind"));
        Assert.Equal("missing QA stage", after.GetMetadata("parkReason"));
    }

    [Fact]
    public async Task Park_TerminalOrBlocked_Conflicts()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "T", Priority: 2), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "boom", default);
        var closed = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "C", Priority: 2), default);
        await _issues.TransitionAsync(closed.Id, IssueStatus.Closed, "done", default);

        // Failed is parkable (operator hold on a failed task)…
        var r1 = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/park", new { });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        // …but a second park (now Blocked) and a Closed task conflict.
        var r2 = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/park", new { });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        var r3 = await _client.PostAsJsonAsync($"/api/tasks/{closed.Id}/park", new { });
        Assert.Equal(HttpStatusCode.Conflict, r3.StatusCode);
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

    [Fact]
    public async Task Close_ObsoleteTask_ClosesAndReportsOperatorClosed()
    {
        // Operator close-obsolete (2026-08-01): retires the task and
        // reports the machine event so the state record ends at
        // Closed rather than a stale Failed.
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "obsolete"), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "breaker",
            new Dictionary<string, object> { ["state"] = "Failed" }, default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/close",
            new { reason = "obsolete: fix already on main", closePr = false });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = (await _issues.GetAsync(task.Id, default))!;
        Assert.Equal(IssueStatus.Closed, after.Status);
        Assert.Equal("Closed", after.GetMetadata("state"));

        // Terminal tasks refuse a second close.
        var again = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/close",
            new { reason = "again", closePr = false });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Close_WithClosePr_ClosesLinkedPrViaProjectGitHub()
    {
        CloseCapturingGitHub.LastClosedPr = null;
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "obsolete",
            Metadata: new Dictionary<string, object> { ["prNumber"] = "769" }), default);
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "breaker", default);

        var resp = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/close",
            new { reason = "obsolete", closePr = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("prClosed").GetBoolean());
        Assert.Equal(769, CloseCapturingGitHub.LastClosedPr);
    }

    private sealed class CloseCapturingGitHub : GitHubService
    {
        public static int? LastClosedPr;
        public CloseCapturingGitHub() : base("o", "r") { }
        public override Task<Octokit.PullRequest> ClosePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            LastClosedPr = prNumber;
            return Task.FromResult(new Octokit.PullRequest());
        }
    }
}
