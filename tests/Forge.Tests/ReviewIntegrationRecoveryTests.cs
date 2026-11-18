using System.Net;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Reviewer;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Forge.Tests;

public sealed class ReviewIntegrationRecoveryTests
{
    [Fact]
    public async Task NormalTaskRetryResumesWatchAndPreservesPublicationAndEngineeringEvidence()
    {
        var hints = new Hints();
        await using var store = new IssueStore(TempRoot.Instance.NewDbPath("review-retry"), "forge", hints);
        var task = await store.CreateAsync(new Forge.Core.NewIssue("task", "Review", Metadata: new Dictionary<string, object>
        { ["prNumber"] = 7, ["reviewDraft"] = "saved", ["reviewPublishStarted"] = true,
          ["reviewErrorAttempts"] = 3, ["reviewIntegrationAttempts"] = 3, ["humanReviewRequired"] = true, ["reworkAttempts"] = 2 }));
        await store.TransitionAsync(task.Id, IssueStatus.Blocked, "review issue", new Dictionary<string, object> { ["blockedKind"] = ReviewIntegrationState.BlockedKind });
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        TaskEndpoints.MapTaskEndpoints(app, store, null, null, NullLogger.Instance);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        var watchWakeups = 0;
        hints.BeforePublish = evt =>
        {
            if (evt is not SweepTick { Kind: SweepKind.Watch } tick) return;
            Assert.Equal("forge", tick.ProjectId);
            var pollGate = PRWatcher.GetPollGate("forge", task.Id);
            var reviewGate = ReviewWorkerAttempt.Gate("forge", task.Id, AgentType.Reviewer);
            Assert.True(pollGate.Wait(0)); pollGate.Release();
            Assert.True(reviewGate.Wait(0)); reviewGate.Release();
            watchWakeups++;
        };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/tasks/{task.Id}/reset-strikes?projectId=forge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/tasks/{task.Id}/requeue?projectId=forge", null)).StatusCode);
        var resumed = (await store.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.InProgress, resumed.Status);
        Assert.Equal("saved", resumed.GetMetadata("reviewDraft"));
        Assert.Equal("True", resumed.GetMetadata("reviewPublishStarted"));
        Assert.Equal("True", resumed.GetMetadata("humanReviewRequired"));
        Assert.Equal("2", resumed.GetMetadata("reworkAttempts"));
        Assert.Equal("0", resumed.GetMetadata("reviewErrorAttempts"));
        Assert.Null(resumed.GetMetadata("blockedKind"));
        Assert.Equal(1, watchWakeups);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/tasks/{task.Id}/requeue?projectId=forge", null)).StatusCode);
        Assert.Equal(1, watchWakeups);
    }

    [Theory]
    [InlineData(404, true, true)] [InlineData(403, true, false)] [InlineData(500, true, false)] [InlineData(404, false, false)]
    public async Task MissingReviewerDoesNotHideOtherDecisionsButOtherFailuresStayClosed(int permissionStatus, bool repositoryVisible, bool succeeds)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var permissionReads = 0;
        var advisoryPermissionReads = 0;
        app.Map("/{**path}", (HttpContext context) =>
        {
        var path = context.Request.Path.Value!;
        if (path.EndsWith("/pulls/7/reviews")) return Results.Json(new[]
        {
            new { id = 3, user = new { login = "advisory", type = "User" }, commit_id = "head", state = "COMMENTED", body = "Suggestion" },
            new { id = 4, user = new { login = "advisory", type = "User" }, commit_id = "head", state = "PENDING", body = "Draft" },
            new { id = 1, user = new { login = "outside", type = "User" }, commit_id = "head", state = "APPROVED", body = "External opinion" },
            new { id = 2, user = new { login = "maintainer", type = "User" }, commit_id = "head", state = "CHANGES_REQUESTED", body = "Must fix" }
        });
        if (path.EndsWith("/collaborators/outside/permission")) { permissionReads++; return Results.StatusCode(permissionStatus); }
        if (path.EndsWith("/collaborators/advisory/permission")) { advisoryPermissionReads++; return Results.StatusCode(500); }
        if (path.EndsWith("/collaborators/maintainer/permission")) return Results.Json(new { permission = "write" });
        if (path.EndsWith("/repos/owner/repo")) return repositoryVisible ? Results.Json(new { name = "repo", full_name = "owner/repo" }) : Results.NotFound();
        return Results.NotFound();
        });
        await app.StartAsync();
        var client = new GitHubClient(new ProductHeaderValue("fixture"), new Uri($"http://127.0.0.1:{app.GetPort()}/"));
        var service = new GitHubService("owner", "repo", client, client);
        if (!succeeds) { await Assert.ThrowsAnyAsync<ApiException>(() => service.GetAuthorizedDecisionsAsync(7, "author")); Assert.Equal(1, permissionReads); Assert.Equal(0, advisoryPermissionReads); return; }
        var decision = Assert.Single(await service.GetAuthorizedDecisionsAsync(7, "author"));
        Assert.Equal("maintainer", decision.Login);
        Assert.Equal(PullRequestReviewState.ChangesRequested, decision.State);
        Assert.Equal(1, permissionReads);
        Assert.Equal(0, advisoryPermissionReads);
    }

    private sealed class Hints : IEventPublisher
    {
        public Action<IForgeEvent>? BeforePublish;
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        { BeforePublish?.Invoke(evt); return Task.CompletedTask; }
    }
}
