using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Forge.Tests;

public sealed class OperatorAcceptanceRepairEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewAndApplyRequireOwningStorePlanningEvidence(bool foreignRuns)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("acceptance-http"), "forge");
        await using var other = new IssueStore(TempRoot.Instance.NewDbPath("acceptance-other"), "alpha");
        var (scope, task, observation) = await Seed(issues);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        OperatorAcceptanceRepairEndpoints.MapOperatorAcceptanceRepairEndpoints(app,
            project => project == "forge" ? issues : null,
            new IssueGroomerRunStore(foreignRuns ? other.Db : issues.Db), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{((IHost)app).GetPort()}") };
        var url = $"/api/specs/{scope.SpecId}/operator-checks/repair?project=forge";
        var response = await http.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(!foreignRuns, response.GetProperty("eligible").GetBoolean());
        var request = foreignRuns ? new MoveOperatorChecksRequest(1, 1, "unavailable")
            : new MoveOperatorChecksRequest(response.GetProperty("preview").GetProperty("specVersion").GetInt32(),
                response.GetProperty("preview").GetProperty("groomRunId").GetInt64(),
                response.GetProperty("preview").GetProperty("previewHash").GetString()!);
        if (!foreignRuns)
        {
            var preview = response.GetProperty("preview");
            Assert.Equal("Observe after deployment", preview.GetProperty("sourceRows")[0].GetProperty("description").GetString());
            Assert.Equal("OperatorObserved", preview.GetProperty("transferredCriteria")[0].GetProperty("evidenceKind").GetString());
            Assert.Equal(2, preview.GetProperty("criteria").GetArrayLength());
            using var stale = await http.PostAsJsonAsync(url, request with { PreviewHash = "stale" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(IssueStatus.Pending, (await issues.GetAsync(observation.Id))!.Status);
        }
        using var applied = await http.PostAsJsonAsync(url, request);
        Assert.Equal(foreignRuns ? HttpStatusCode.Conflict : HttpStatusCode.OK, applied.StatusCode);
        var after = (await issues.GetAsync(observation.Id))!;
        Assert.Equal(foreignRuns ? IssueStatus.Pending : IssueStatus.Closed, after.Status);
        Assert.Equal(IssueStatus.Pending, (await issues.GetAsync(task.Id))!.Status);
        using var wrongProject = await http.PostAsJsonAsync(url.Replace("project=forge", "project=alpha"), request);
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task SpecEndpointPrimaryFallbackCannotApplyForeignProjectRoute()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("acceptance-fallback"), "forge");
        var (scope, _, observation) = await Seed(issues);
        var preview = (await new Forge.Core.OperatorAcceptanceRepair(issues).PreviewAsync(scope.SpecId)).Preview!;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        SpecEndpoints.MapSpecEndpoints(app, new SpecStore(issues), new NullSpecExtractionReader(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, issues: issues, groomerRuns: new(issues.Db));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{((IHost)app).GetPort()}") };
        var url = $"/api/specs/{scope.SpecId}/operator-checks/repair?project=alpha";
        using var get = await http.GetAsync(url);
        using var post = await http.PostAsJsonAsync(url, new MoveOperatorChecksRequest(preview.SpecVersion, preview.GroomRunId, preview.PreviewHash));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode); Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(IssueStatus.Pending, (await issues.GetAsync(observation.Id))!.Status);
        await app.StopAsync();
    }

    private static async Task<(SprintScopeReference Scope, IssueRecord Task, IssueRecord Observation)> Seed(IssueStore issues)
    {
        var scope = await ApprovedSprintFixture.Scope(issues);
        var specs = new SpecStore(issues);
        await specs.SetStatusAsync(scope.SpecId, SpecStatus.Approved);
        await specs.SetStatusAsync(scope.SpecId, SpecStatus.Grooming);
        var runs = new IssueGroomerRunStore(issues.Db);
        var run = await runs.StartAsync(scope.SpecId, GroomerTriggerKind.Scheduled, default);
        var story = await issues.CreateAsync(new("story", "Useful delivery", ParentId: scope.SpecId));
        var task = await issues.CreateAsync(new("task", "Implement feature", "Offline implementation", ParentId: story.Id));
        var qa = await issues.CreateAsync(new("qa", "Qualify feature", "Observe after deployment", ParentId: story.Id));
        await ApprovedSprintFixture.Bind(issues, scope, [task]);
        var binding = new SprintOutcomeBinding(1, "observations", "Qualify running feature",
            [new("visual", "Observe desktop and narrow display", SprintEvidenceKind.OperatorObserved, scope)],
            [qa.Id], ["visual"], SprintMemberReason.Delivers, scope);
        await issues.MergePendingMetadataAsync(qa.Id, new Dictionary<string, object>
        { [SprintPlanJson.MetadataKey] = JsonSerializer.SerializeToElement(binding, SprintPlanJson.Options) });
        await issues.AddDependencyAsync(task.Id, qa.Id, IssueDepKind.Blocks);
        await specs.SetStatusAsync(scope.SpecId, SpecStatus.Groomed);
        await runs.FinishAsync(run.Id, GroomerRunStatus.Succeeded, 1, 2, null, TimeSpan.Zero, default);
        return (scope, task, qa);
    }
}
