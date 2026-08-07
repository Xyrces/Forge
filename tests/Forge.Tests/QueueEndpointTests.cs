using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// GET /api/queue — the engineering ready queue in claim order
/// (blocker boost → priority → FIFO) with per-item wait reasons.
/// Sprint members only; blocked tasks absent by construction.
/// </summary>
public class QueueEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SprintStore _sprints;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public QueueEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-queue-api-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _sprints = new SprintStore(_issues);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(_dbPath) ?? Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        QueueEndpoints.MapQueueEndpoints(app, _issues, _sprints,
            projectContexts: null, slots: null, llmConfig: null,
            modelOverrides: null, rateLimits: null);
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

    private async Task<IssueRecord> GroomedTaskAsync(string title, int priority = 3)
        => await _issues.CreateAsync(new NewIssue(Type: "task", Title: title, Priority: priority,
            Metadata: new Dictionary<string, object> { ["groomed"] = "true" }));

    private async Task<JsonElement> GetQueueAsync()
        => await _client.GetFromJsonAsync<JsonElement>("/api/queue");

    [Fact]
    public async Task Queue_NoActiveSprint_Empty()
    {
        await GroomedTaskAsync("orphan");
        var q = await GetQueueAsync();
        Assert.Equal(0, q.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Queue_MembersInClaimOrder_WithBoostFlag()
    {
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(7)));
        var p1 = await GroomedTaskAsync("p1 plain", priority: 1);
        var blocker = await GroomedTaskAsync("blocker", priority: 3);
        var blocked = await GroomedTaskAsync("blocked", priority: 1);
        await _issues.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        foreach (var id in new[] { p1.Id, blocker.Id, blocked.Id })
        {
            await _sprints.AddIssueAsync(sprint.Id, id);
        }

        var q = await GetQueueAsync();
        var items = q.GetProperty("items").EnumerateArray().ToArray();

        // Blocked member absent; blocker boosted ahead of the P1.
        Assert.Equal(2, items.Length);
        Assert.Equal(blocker.Id, items[0].GetProperty("issueId").GetString());
        Assert.True(items[0].GetProperty("boosted").GetBoolean());
        Assert.Equal(p1.Id, items[1].GetProperty("issueId").GetString());
        Assert.False(items[1].GetProperty("boosted").GetBoolean());
        Assert.Equal(1, items[0].GetProperty("position").GetInt32());
        Assert.Equal("ready", items[0].GetProperty("wait").GetString());
    }

    [Fact]
    public async Task Queue_UngroomedMember_AwaitingGroom()
    {
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(7)));
        var fresh = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "sprint-born blocker"));
        await _sprints.AddIssueAsync(sprint.Id, fresh.Id);

        var q = await GetQueueAsync();
        var item = Assert.Single(q.GetProperty("items").EnumerateArray());
        Assert.Equal("awaiting-groom", item.GetProperty("wait").GetString());
    }

    [Fact]
    public async Task Queue_NonMemberTask_Excluded()
    {
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(7)));
        var member = await GroomedTaskAsync("member");
        await GroomedTaskAsync("outsider");
        await _sprints.AddIssueAsync(sprint.Id, member.Id);

        var q = await GetQueueAsync();
        var item = Assert.Single(q.GetProperty("items").EnumerateArray());
        Assert.Equal(member.Id, item.GetProperty("issueId").GetString());
    }

    [Fact]
    public async Task Queue_BlockedMembers_ListedSeparatelyWithBlockers()
    {
        var sprint = await _sprints.CreateAsync(new NewSprint(
            Name: "S", Goal: "g", StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(7)));
        var blocker = await GroomedTaskAsync("blocker", priority: 3);
        var blocked = await GroomedTaskAsync("blocked", priority: 1);
        await _issues.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        await _sprints.AddIssueAsync(sprint.Id, blocker.Id);
        await _sprints.AddIssueAsync(sprint.Id, blocked.Id);

        var q = await GetQueueAsync();

        // The blocker is claimable; the blocked member is NOT an item
        // but appears in the reconciliation section with its blockers.
        var item = Assert.Single(q.GetProperty("items").EnumerateArray());
        Assert.Equal(blocker.Id, item.GetProperty("issueId").GetString());
        var row = Assert.Single(q.GetProperty("blocked").EnumerateArray());
        Assert.Equal(blocked.Id, row.GetProperty("issueId").GetString());
        Assert.Equal(blocker.Id, row.GetProperty("blockedBy")[0].GetString());
    }
}
