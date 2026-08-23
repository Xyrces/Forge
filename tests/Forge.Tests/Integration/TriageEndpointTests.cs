using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

public sealed class TriageEndpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly FailureTriageStore _triage;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public TriageEndpointTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("triage-ep");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _triage = new FailureTriageStore(_issues);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        TriageEndpoints.MapTriageEndpoints(app, _issues, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _issues.Dispose();
    }

    private async Task SeedAsync()
    {
        // Two 429s on distinct tasks + one no-diff, all inside 7d.
        var t1 = await _triage.OpenAsync("task-1", DateTime.UtcNow.AddHours(-2), "llm-429-quota", "transient-upstream", "HTTP 429 a");
        await _triage.OpenAsync("task-2", DateTime.UtcNow.AddHours(-1), "llm-429-quota", "transient-upstream", "HTTP 429 b");
        await _triage.OpenAsync("task-3", DateTime.UtcNow.AddMinutes(-30), "no-diff-bounce", "no-progress", "no diff in 3 attempts");
        // Actioned + proven row: not open.
        await _triage.RecordActionAsync(t1, FailureTriageActions.OperatorRequeue, "operator", DateTime.UtcNow.AddMinutes(-50), FailureTriageOutcomes.Pending);
        await _triage.CloseOutcomeAsync(t1, FailureTriageOutcomes.Succeeded);
    }

    [Fact]
    public async Task Ledger_GroupsBySignature_WithSummaryAndHealth()
    {
        await SeedAsync();

        var resp = await _client.GetAsync("/api/triage/ledger");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var summary = root.GetProperty("summary");
        // task-2 (unactioned) + task-3 (unactioned) are open; task-1 succeeded.
        Assert.Equal(2, summary.GetProperty("openFailures").GetInt32());
        Assert.Equal(2, summary.GetProperty("distinctSignatures7d").GetInt32());
        Assert.Equal(0, summary.GetProperty("escalations7d").GetInt32());
        Assert.Equal(5, summary.GetProperty("escalationBudget").GetInt32());
        Assert.Equal(7, summary.GetProperty("dailyOpenFailures7d").GetArrayLength());

        var groups = root.GetProperty("groups");
        Assert.Equal(2, groups.GetArrayLength());
        var quota = groups.EnumerateArray().Single(g => g.GetProperty("signature").GetString() == "llm-429-quota");
        Assert.Equal(2, quota.GetProperty("count").GetInt32());
        Assert.Equal(2, quota.GetProperty("distinctTasks").GetInt32());
        Assert.Equal("transient-upstream", quota.GetProperty("classification").GetString());
        Assert.Equal("task-2", quota.GetProperty("lastTaskId").GetString());
        Assert.False(quota.GetProperty("bugSuspect").GetBoolean());

        var health = root.GetProperty("health");
        Assert.Equal(1, health.GetProperty("noDiffBounces7d").GetInt32());
        Assert.Equal(0, health.GetProperty("planGateRejections7d").GetInt32());
    }

    [Fact]
    public async Task Ledger_BugSuspect_AtThreeDistinctTasks()
    {
        for (var i = 1; i <= 3; i++)
            await _triage.OpenAsync($"task-{i}", DateTime.UtcNow, "session-pairing-400", "state-poison", "tool_calls must be followed");

        using var doc = JsonDocument.Parse(await (await _client.GetAsync("/api/triage/ledger")).Content.ReadAsStringAsync());
        var group = doc.RootElement.GetProperty("groups").EnumerateArray().Single();
        Assert.True(group.GetProperty("bugSuspect").GetBoolean());
        Assert.Equal(3, group.GetProperty("distinctTasks").GetInt32());
    }

    [Fact]
    public async Task SignatureDrilldown_ReturnsRowsWithTitles()
    {
        await _issues.CreateAsync(new NewIssue(Type: "task", Title: "fix the thing"));
        await _triage.OpenAsync("task-1", DateTime.UtcNow, "gateway-5xx", "transient-upstream", "503 from gateway");
        await _triage.OpenAsync("task-2", DateTime.UtcNow, "other", "unclassified", null);

        var resp = await _client.GetAsync("/api/triage/ledger/gateway-5xx");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal("task-1", row.GetProperty("taskId").GetString());
        Assert.Equal("fix the thing", row.GetProperty("taskTitle").GetString());
        Assert.Equal("503 from gateway", row.GetProperty("errorExcerpt").GetString());
    }

    [Fact]
    public async Task SignatureDrilldown_UnknownSignature_ReturnsEmpty()
    {
        var resp = await _client.GetAsync("/api/triage/ledger/nope");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.EnumerateArray());
    }
}
