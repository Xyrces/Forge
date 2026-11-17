using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Forge.Tests;

public sealed class CompletionEndpointTests : IAsyncLifetime
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("completion-api"), "project");
    private WebApplication app = null!;
    private HttpClient client = null!;
    private SprintCompletionRecord receipt = null!;
    private CompletionTriageBatch batch = null!;
    private const string Token = "completion-api-private-test-token-long-enough";

    public async Task InitializeAsync()
    {
        var task = await issues.CreateAsync(new NewIssue("task", "Legacy work"));
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("History", "History", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        var store = new SprintCompletionStore(issues);
        receipt = await store.BeginCompletionAsync(sprint.Id, [(await issues.GetAsync(task.Id))!], new(null, null, EngineeringOutcome.LegacyTerminalOnly, "{}"));
        var drafts = new FollowUpDraftStore(issues);
        var draft = await drafts.FileAsync(new(0, sprint.Id, task.Id, "reviewer", "Finding", "Detail", 2, null, DateTime.UtcNow, null));
        batch = await store.ClaimBatchAsync(receipt.Id, [(await drafts.GetAsync(draft))!]);
        await store.TryBeginTriageAsync(batch.Id);
        await store.RecoverRunningAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = TempRoot.Instance.NewDirectory("completion-host"), ApplicationName = "Forge.Tests"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        new DashboardMutationAuthentication(Token).Protect(app);
        CompletionEndpoints.MapCompletionEndpoints(app, issues, null, _ => Task.FromResult<string?>(new string('a', 40)));
        await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}/") };
    }

    [Fact]
    public async Task ReadProjectionIsTruthfulBoundedAndProjectScoped()
    {
        var detail = await client.GetFromJsonAsync<CompletionDetail>($"api/sprints/{receipt.SprintId}/completion?projectId=project");
        Assert.Equal("LegacyTerminalOnly", detail!.Receipt.Outcome);
        Assert.False(detail.Receipt.ReadyForDeployment);
        Assert.Equal("NeedsReview", Assert.Single(detail.Batches).State);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/sprints/{receipt.SprintId}/completion?projectId=other")).StatusCode);
        var json = await client.GetStringAsync("api/completions?projectId=project&limit=1");
        Assert.DoesNotContain("membersJson", json);
        Assert.DoesNotContain("sourcesJson", json);
        Assert.Contains(receipt.Id, json);
    }

    [Fact]
    public async Task RetryRequiresAuthenticationOwnershipAndNeedsReview()
    {
        var route = $"api/sprints/{receipt.SprintId}/completion/triage/{batch.Id}/retry?projectId=project";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(route, null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(route.Replace("projectId=project", "projectId=other"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(route, null)).StatusCode);
        Assert.Equal("Pending", Assert.Single(await new SprintCompletionStore(issues).ListBatchesAsync(receipt.Id)).State);
    }

    [Fact]
    public async Task OperatorObservationRejectsUnauthenticatedAndWrongRunningRevision()
    {
        var route = $"api/sprints/{receipt.SprintId}/completion/operator-evidence?projectId=project";
        var evidence = new OperatorCriterionEvidence("plan", "visual", new string('b', 40), "deployment", "proof", new string('c', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(route, evidence)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.PostAsJsonAsync(route, evidence);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("running dashboard revision", await response.Content.ReadAsStringAsync());
        Assert.Equal(EngineeringOutcome.LegacyTerminalOnly, (await new SprintCompletionStore(issues).GetAsync(receipt.Id))!.EngineeringOutcome);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        issues.Dispose();
    }
}
