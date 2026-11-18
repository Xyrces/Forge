using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Dashboard;
using Forge.Reviewer;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class TaskReviewEndpointTests : IAsyncLifetime
{
    private const string Token = "review-operator-test-token-at-least-32-chars";
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("review-endpoints"), "forge");
    private readonly Hints hints = new();
    private WebApplication app = null!;
    private HttpClient client = null!;
    private IssueRecord task = null!;

    public async Task InitializeAsync()
    {
        task = await issues.CreateAsync(new("task", "Current work", Metadata: new Dictionary<string, object> { ["reworkAttempts"] = "2" }));
        await issues.ClaimAsync(task.Id, "worker");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        new DashboardMutationAuthentication(Token).Protect(app);
        TaskReviewEndpoints.MapTaskReviewEndpoints(app, issues, null, hints, NullLogger.Instance);
        await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}/") };
    }
    private string HoldPath => $"/api/tasks/{task.Id}/human-review?projectId=forge";
    private void Authenticate() => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

    [Fact]
    public async Task PrePrHoldRequiresAuthentication_IsDurable_AndDoesNotResetWorker()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(HoldPath, new { required = true })).StatusCode);
        Authenticate();
        var before = (await issues.GetAsync(task.Id))!;
        hints.BeforePublish = evt =>
        {
            var gate = PRWatcher.GetPollGate("forge", task.Id);
            Assert.True(gate.Wait(0), "Wake hint must be sent after the endpoint releases the watcher reservation.");
            gate.Release();
        };
        (await client.PutAsJsonAsync(HoldPath, new { required = true })).EnsureSuccessStatusCode();
        var after = (await issues.GetAsync(task.Id))!;
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Assignee, after.Assignee);
        Assert.Equal(before.DispatchCheckpoint, after.DispatchCheckpoint);
        Assert.Equal("2", after.GetMetadata("reworkAttempts"));
        Assert.True(bool.Parse(after.GetMetadata("humanReviewRequired")!));
        Assert.True(bool.Parse(after.GetMetadata("humanReviewSyncPending")!));
        await using var memory = new MemoryStore(issues.Db);
        Assert.Equal(new HumanReviewHold(true, true), await new PullRequestReviewPolicy(memory).GetHoldAsync(task.Id, default));
        Assert.Equal(task.Id, Assert.Single(hints.Events.OfType<PrWatchRequested>()).TaskId);
        var audit = Assert.Single((await issues.ListEventsAsync(task.Id)).Where(e => e.Kind == "human_review"));
        Assert.Contains("authenticated", audit.Actor);
        Assert.Contains("required", audit.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(HoldPath.Replace("projectId=forge", "projectId=other"), new { required = true })).StatusCode);
    }

    [Fact]
    public async Task HoldWaitsForMergeReservation_ThenRejectsCompletedTask()
    {
        Authenticate();
        var gate = PRWatcher.GetPollGate("forge", task.Id);
        await gate.WaitAsync();
        Task<HttpResponseMessage> request;
        try
        {
            request = client.PutAsJsonAsync(HoldPath, new { required = true });
            await Task.Delay(50);
            Assert.False(request.IsCompleted);
            await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        }
        finally { gate.Release(); }
        Assert.Equal(HttpStatusCode.Conflict, (await request).StatusCode);
        await using var memory = new MemoryStore(issues.Db);
        Assert.False((await new PullRequestReviewPolicy(memory).GetHoldAsync(task.Id, default)).Required);
    }

    [Fact]
    public async Task ProjectModeIsScopedValidatedAndSurvivesAdditionalTaskHoldRelease()
    {
        Authenticate();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/projects/forge/review-policy", new { mode = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/projects/other/review-policy", new { mode = "Human" })).StatusCode);
        (await client.PutAsJsonAsync("/api/projects/forge/review-policy", new { mode = "Both" })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(HoldPath, new { required = false })).EnsureSuccessStatusCode();
        await using var memory = new MemoryStore(issues.Db);
        var policy = new PullRequestReviewPolicy(memory);
        Assert.Equal(RequiredReview.Both, await policy.GetModeAsync(default));
        Assert.False((await policy.GetHoldAsync(task.Id, default)).Required);
        Assert.True((await policy.GetHoldAsync(task.Id, default)).SyncPending);
        Assert.Single(hints.Events.OfType<SweepTick>());
        Assert.NotEmpty(await memory.RecallAsync("review/project-mode-audit/"));
        var json = await client.GetStringAsync("/api/projects/forge/review-policy");
        Assert.Contains("Both", json);
    }

    [Fact]
    public async Task NominatedHumanRequiresAuthenticationAndExplicitClear()
    {
        const string path="/api/projects/forge/review-policy";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PutAsJsonAsync(path,new { mode="Both",nominatedHuman="Owner" })).StatusCode);
        Authenticate();
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(path,new { mode="Both",nominatedHuman="app[bot]" })).StatusCode);
        (await client.PutAsJsonAsync(path,new { mode="Both",nominatedHuman="Owner" })).EnsureSuccessStatusCode();
        Assert.Contains("Owner",await client.GetStringAsync(HoldPath));
        (await client.PutAsJsonAsync(path,new { mode="Human" })).EnsureSuccessStatusCode();
        Assert.Contains("Owner",await client.GetStringAsync(path));
        (await client.PutAsJsonAsync(path,new { mode="Human",nominatedHuman=(string?)null })).EnsureSuccessStatusCode();
        await using var memory=new MemoryStore(issues.Db);
        var setting=await new PullRequestReviewPolicy(memory).GetSettingsAsync(default);
        Assert.Equal(RequiredReview.Human,setting.Mode);Assert.Null(setting.NominatedHuman);
    }

    public async Task DisposeAsync()
    {
        client.Dispose(); await app.DisposeAsync(); await issues.DisposeAsync();
    }
    private sealed class Hints : IEventPublisher
    {
        public List<IForgeEvent> Events { get; } = [];
        public Action<IForgeEvent>? BeforePublish { get; set; }
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        { BeforePublish?.Invoke(evt); Events.Add(evt); return Task.CompletedTask; }
    }
}
