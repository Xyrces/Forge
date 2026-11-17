using System.Net;
using System.Net.Http.Json;
using Forge.Core;
using Forge.Agents;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.AI;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Sprint;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class TaskRegroomTests
{
    private sealed record Fixture(IssueStore Issues, SprintStore Sprints, SprintRecord Sprint, IssueRecord Root, IssueRecord Extra, SprintScopeReference Scope);
    private static async Task<Fixture> Seed(bool expanded = true)
    {
        var issues = new IssueStore(TempRoot.Instance.NewDbPath("regroom"), "forge");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var root = await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues, scope, [root]);
        var sprints = new SprintStore(issues);
        var snapshot = (await new SprintPlanBuilder(issues, sprints, new SpecStore(issues), "forge", null).BuildAsync()).Snapshot!;
        var sprint = await sprints.CreateWithIssuesAsync(new("Delivery", "Delivery", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        var extra = await ApprovedSprintFixture.Task(issues);
        // Legacy bad grooming was accepted before the new active-prerequisite guard.
        await ApprovedSprintFixture.Bind(issues, scope, [extra], criterion: expanded ? "duplicate-handoff" : "behavior");
        await issues.AddDependencyAsync(extra.Id, root.Id, IssueDepKind.Blocks);
        await sprints.AddIssueAsync(sprint.Id, extra.Id); // Visibility is not committed plan membership.
        return new(issues, sprints, sprint, root, extra, scope);
    }
    private static TaskRegroomRequest Request(TaskRegroomAvailability p) => new(p.Snapshot!.UpdatedAt, p.Snapshot.BindingHash, "Re-evaluate against exact committed acceptance");

    [Fact]
    public async Task ExplicitRegroomPreservesBindingApprovalEdgesAndHistoryThenNormalGuardRequiresExactCriteria()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var before = (await issues.GetAsync(f.Extra.Id))!;
        var rootBefore = (await issues.GetAsync(f.Root.Id))!;
        var preview = await issues.PreviewRegroomAsync(f.Extra.Id);
        Assert.True(preview.Eligible);
        await issues.RequestRegroomAsync(f.Extra.Id, Request(preview), "operator");
        var after = (await issues.GetAsync(f.Extra.Id))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.False(bool.Parse(after.GetMetadata("groomed")!));
        Assert.Equal(before.GetMetadata(SprintPlanJson.MetadataKey), after.GetMetadata(SprintPlanJson.MetadataKey));
        Assert.Equal(rootBefore, await issues.GetAsync(f.Root.Id));
        var audit = Assert.Single((await issues.ListEventsAsync(f.Extra.Id)).Where(e => e.Kind == "operator_regroom_requested"));
        Assert.Contains("duplicate-handoff", audit.Detail);
        Assert.Contains("operator", audit.Detail);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.RequestRegroomAsync(f.Extra.Id, Request(preview), "operator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.MergePendingMetadataAsync(f.Extra.Id, new Dictionary<string, object> { ["groomed"] = true }));
        Assert.False(bool.Parse((await issues.GetAsync(f.Extra.Id))!.GetMetadata("groomed")!));
        await ApprovedSprintFixture.Bind(issues, f.Scope, [f.Extra]);
        await issues.MergePendingMetadataAsync(f.Extra.Id, new Dictionary<string, object> { ["groomed"] = true });
        var snapshot = await new SprintActivationValidator(issues, f.Sprints, new SpecStore(issues), "forge", null)
            .ValidateAsync([f.Extra.Id], default, activating: false);
        await f.Sprints.AddValidatedPrerequisiteAsync(f.Sprint.Id, snapshot);
        Assert.Contains((await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(f.Sprint.Id))!.Members, m => m.TaskId == f.Extra.Id);
        Assert.False((await issues.PreviewRegroomAsync(f.Extra.Id)).Eligible);
    }

    [Theory]
    [InlineData("claimed")]
    [InlineData("binding")]
    [InlineData("published")]
    [InlineData("history")]
    public async Task StaleOrExecutedTaskCannotBeReset(string change)
    {
        var f = await Seed(); await using var issues = f.Issues;
        var preview = await issues.PreviewRegroomAsync(f.Extra.Id);
        if (change == "claimed") await issues.ClaimAsync(f.Extra.Id, "coredev");
        if (change == "binding") await issues.MergePendingMetadataAsync(f.Extra.Id, new Dictionary<string, object> { ["groomNote"] = "Changed decision" });
        if (change == "published") await issues.MergePendingMetadataAsync(f.Extra.Id, new Dictionary<string, object> { ["prNumber"] = 123 });
        if (change == "history") await issues.AddEventAsync(f.Extra.Id, "claimed", "old execution");
        var before = await issues.GetAsync(f.Extra.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.RequestRegroomAsync(f.Extra.Id, Request(preview), "operator"));
        Assert.Equal(before, await issues.GetAsync(f.Extra.Id));
        Assert.DoesNotContain(await issues.ListEventsAsync(f.Extra.Id), e => e.Kind == "operator_regroom_requested");
    }

    [Fact]
    public async Task AmendmentCommittedAfterPreviewPreventsRegroom()
    {
        var f = await Seed(false); await using var issues = f.Issues;
        var preview = await issues.PreviewRegroomAsync(f.Extra.Id);
        var snapshot = await new SprintActivationValidator(issues, f.Sprints, new SpecStore(issues), "forge", null)
            .ValidateAsync([f.Extra.Id], default, activating: false);
        await f.Sprints.AddValidatedPrerequisiteAsync(f.Sprint.Id, snapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.RequestRegroomAsync(f.Extra.Id, Request(preview), "operator"));
        Assert.True(bool.Parse((await issues.GetAsync(f.Extra.Id))!.GetMetadata("groomed")!));
        Assert.DoesNotContain(await issues.ListEventsAsync(f.Extra.Id), e => e.Kind == "operator_regroom_requested");
    }

    [Fact]
    public async Task ConcurrentSameRequestHasOneMutationAndOneAudit()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var request = Request(await issues.PreviewRegroomAsync(f.Extra.Id));
        async Task<bool> Apply()
        {
            try { await issues.RequestRegroomAsync(f.Extra.Id, request, "operator"); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Apply(), Apply());
        Assert.Single(outcomes, applied => applied);
        Assert.Single((await issues.ListEventsAsync(f.Extra.Id)).Where(e => e.Kind == "operator_regroom_requested"));
    }

    [Fact]
    public async Task AuditFailureRollsBackEligibilityReset()
    {
        var f = await Seed(); await using var issues = f.Issues;
        var preview = await issues.PreviewRegroomAsync(f.Extra.Id);
        var before = await issues.GetAsync(f.Extra.Id);
        await using var c = await issues.Db.OpenAsync(); using var q = c.CreateCommand();
        q.CommandText = "CREATE TRIGGER regroom_fail BEFORE INSERT ON issue_event WHEN NEW.kind='operator_regroom_requested' BEGIN SELECT RAISE(ABORT,'audit failure'); END";
        await q.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => issues.RequestRegroomAsync(f.Extra.Id, Request(preview), "operator"));
        Assert.Equal(before, await issues.GetAsync(f.Extra.Id));
    }

    [Fact]
    public async Task ForeignProjectCannotUseApprovalOrEndpointFallback()
    {
        var f = await Seed(); await using var issues = f.Issues;
        await using var other = new IssueStore(issues.Db, "other");
        Assert.False((await other.PreviewRegroomAsync(f.Extra.Id)).Eligible);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        TaskRegroomEndpoints.MapTaskRegroomEndpoints(app, issues, null, NullLogger.Instance);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{((IHost)app).GetPort()}") };
        var url = $"/api/tasks/{f.Extra.Id}/regroom?projectId=forge";
        var p = (await http.GetFromJsonAsync<TaskRegroomAvailability>(url))!;
        Assert.True(p.Eligible);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url.Replace("forge", "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync(url.Replace("forge", "other"), Request(p))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync(url, Request(p) with { ExpectedBindingHash = "stale" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync(url, Request(p))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync(url, Request(p))).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task NormalGroomingReceivesExactCommittedCriteriaAndOperatorReason()
    {
        var f = await Seed(); await using var issues = f.Issues;
        await issues.RequestRegroomAsync(f.Extra.Id, Request(await issues.PreviewRegroomAsync(f.Extra.Id)), "operator");
        var client = new CaptureClient();
        var groomer = new GroomerAgent(issues, new SpecStore(issues), new InMemoryDashboardEventBus(),
            new ScriptingChatClientFactory(client), new LlmConfig(new ProviderConfig("test", "", null, null, "test")),
            NullLogger<GroomerAgent>.Instance, projectId: "forge");
        await Assert.ThrowsAsync<OperationCanceledException>(() => groomer.GroomTaskAsync(f.Extra.Id));
        var plan = (await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(f.Sprint.Id))!;
        Assert.Contains(plan.Hash, client.Prompt);
        Assert.Contains(SprintPlanJson.Serialize(plan.Criteria), client.Prompt);
        Assert.Contains("Do not add a duplicate handoff criterion", client.Prompt);
        Assert.Contains("Operator re-groom reason: Re-evaluate against exact committed acceptance", client.Prompt);
    }

    private sealed class CaptureClient() : DelegatingChatClient(new MultiToolCallingChatClient([], "unused"))
    {
        public string Prompt { get; private set; } = "";
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompt = options?.Instructions + "\n" + string.Join("\n", messages.Select(m => m.Text));
            throw new OperationCanceledException("Captured actual grooming input without generation");
        }
    }

    [Fact]
    public async Task NewlyProposedExpandedMappingRejectedBeforePersistence()
    {
        var f = await Seed(false); await using var issues = f.Issues;
        var before = await issues.GetAsync(f.Extra.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovedSprintFixture.Bind(issues, f.Scope, [f.Extra], criterion: "invented-criterion"));
        Assert.Equal(before, await issues.GetAsync(f.Extra.Id));
    }
}
