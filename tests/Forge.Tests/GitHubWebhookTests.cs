using System.Net;
using System.Security.Cryptography;
using System.Text;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Dashboard;
using Forge.Orchestrator.Consumers;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Forge.Tests;

public sealed class GitHubWebhookTests : IAsyncLifetime
{
    private const string Secret = "dedicated-webhook-test-secret-at-least-32-bytes";
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("webhook"), "forge");
    private readonly Events events = new();
    private WebApplication app = null!;
    private HttpClient client = null!;
    private const string PrPayload = "{\"repository\":{\"full_name\":\"Xyrces/Forge\"},\"action\":\"labeled\",\"number\":150,\"pull_request\":{\"number\":150}}";

    public async Task InitializeAsync()
    {
        var projects = new ProjectStore(issues);
        await projects.UpsertAsync(new("forge", "Forge", "https://github.com/Xyrces/Forge.git"));
        await projects.UpsertAsync(new("other", "Other", "https://github.com/Xyrces/Other"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        new DashboardMutationAuthentication("different-operator-token-at-least-32-characters").Protect(app);
        GitHubWebhookEndpoints.MapGitHubWebhookEndpoints(app, Secret, projects, events);
        await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}/") };
    }

    private async Task<HttpResponseMessage> Send(string payload, string kind = "pull_request", Guid? delivery = null,
        string? signPayload = null, bool signed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoints.Route);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-GitHub-Event", kind);
        request.Headers.Add("X-GitHub-Delivery", (delivery ?? Guid.NewGuid()).ToString());
        if (signed) request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(signPayload ?? payload))));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ValidSignatureRoutesOnlyRegisteredRepository_AndDuplicateDeliveryIsCoalesced()
    {
        var delivery = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await Send(PrPayload, delivery: delivery)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(PrPayload, delivery: delivery)).StatusCode);
        var hint = Assert.Single(events.Hints);
        Assert.Equal("forge", hint.ProjectId);
        Assert.Equal(150, hint.PrNumber);
        Assert.Null(hint.TaskId);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(PrPayload.Replace("Xyrces/Forge", "unregistered/repo"))).StatusCode);
        Assert.Single(events.Hints);
    }

    [Fact]
    public async Task MissingOrAlteredSignatureCannotReachTheBus_AndMalformedBodyIsRejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(PrPayload, signed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(PrPayload + " ", signPayload: PrPayload)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("{")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(new string('x', GitHubWebhookEndpoints.MaxBodyBytes + 1))).StatusCode);
        Assert.Empty(events.Hints);
    }

    [Theory]
    [InlineData("pull_request_review", "{\"action\":\"dismissed\",\"pull_request\":{\"number\":150}}", 150, null, null)]
    [InlineData("pull_request_review", "{\"action\":\"submitted\",\"pull_request\":{\"number\":150}}", 150, null, null)]
    [InlineData("pull_request_review_thread", "{\"action\":\"resolved\",\"pull_request\":{\"number\":150},\"thread\":{\"id\":12}}", 150, null, null)]
    [InlineData("pull_request_review_thread", "{\"action\":\"unresolved\",\"pull_request\":{\"number\":150},\"thread\":{\"id\":12}}", 150, null, null)]
    [InlineData("push", "{\"ref\":\"refs/heads/main\",\"after\":\"head\"}", null, "head", "main")]
    [InlineData("status", "{\"sha\":\"head\"}", null, "head", null)]
    [InlineData("check_suite", "{\"check_suite\":{\"head_sha\":\"head\",\"head_branch\":\"feature\"}}", null, "head", "feature")]
    [InlineData("check_run", "{\"check_run\":{\"head_sha\":\"head\",\"pull_requests\":[{\"number\":150}]}}", 150, "head", null)]
    public async Task GitHubEventsProduceHintsOnly(string kind, string fields, int? pr, string? sha, string? branch)
    {
        var payload = "{\"repository\":{\"full_name\":\"Xyrces/Forge\"}," + fields[1..];
        Assert.Equal(HttpStatusCode.Accepted, (await Send(payload, kind)).StatusCode);
        Assert.Contains(events.Hints, h => h.PrNumber == pr && h.HeadSha == sha && h.Branch == branch);
        Assert.All(events.Hints, h =>
        {
            Assert.Equal("forge", h.ProjectId);
            Assert.Equal("github:" + kind, h.Reason);
        });
    }

    [Fact]
    public async Task DelayedPrHintMatchesOwningPr_ButUnknownHeadAndUnrelatedBranchDoNot()
    {
        var task = await issues.CreateAsync(new("task", "Target", Metadata: new Dictionary<string, object>
        { ["prNumber"] = "150", ["branch"] = "feature", ["branchSha"] = "current" }));
        var hint = new PrWatchRequested { ProjectId = "forge", MessageId = "delivery", Reason = "check", HeadSha = "old" };
        Assert.False(PrWatchRequestedConsumer.Matches(hint, task, "main"));
        Assert.False(PrWatchRequestedConsumer.Matches(hint with { Branch = "unrelated" }, task, "main"));
        Assert.True(PrWatchRequestedConsumer.Matches(hint with { PrNumber = 150 }, task, "main"));
        Assert.True(PrWatchRequestedConsumer.Matches(hint with { Branch = "main" }, task, "main"));
        await PrWatchWakeup.RequestAsync(events, "forge", task.Id, "human review hold changed");
        Assert.Equal(task.Id, Assert.Single(events.Hints).TaskId);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        await issues.DisposeAsync();
    }

    private sealed class Events : IEventPublisher
    {
        public List<PrWatchRequested> Hints { get; } = [];
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        {
            if (evt is PrWatchRequested hint) Hints.Add(hint);
            return Task.CompletedTask;
        }
    }
}
