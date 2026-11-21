using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests.Integration;

public sealed class GroomingRecoveryEndpointTests : IDisposable
{
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly IssueGroomerRunStore _runs;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public GroomingRecoveryEndpointTests()
    {
        var path = TempRoot.Instance.NewDbPath("groom-http");
        _issues = new IssueStore(path, "p");
        _specs = new SpecStore(_issues);
        _runs = new IssueGroomerRunStore(path);
        var factory = new GroomerAgentFactory(_issues, _specs, new InMemoryDashboardEventBus(),
            new ScriptingChatClientFactory(new MultiToolCallingChatClient([], "No generation should run in this test.")),
            new LlmConfig(new ProviderConfig("test", "", null, null, "test")), NullLoggerFactory.Instance);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "Forge.Tests" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        SpecEndpoints.MapSpecEndpoints(app, _specs, new NullSpecExtractionReader(), NullLogger<DashboardHost>.Instance,
            groomerFactory: factory, groomerRuns: _runs, issues: _issues);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryRequiresLatestInterruptedAttempt_QueuesWithoutExecuting(bool enteredGrooming)
    {
        var spec = await _specs.CreateAsync(new NewSpec("p", "Spec", "Body"));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        var run = await _runs.StartAsync(spec.Id, GroomerTriggerKind.Scheduled, default);
        if (enteredGrooming) await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming);
        await _runs.RecoverInterruptedAsync();
        var actions = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{spec.Id}/actions?project=p");
        Assert.True(actions.GetProperty("groomingRetryAvailable").GetBoolean());
        Assert.False(actions.GetProperty("canStartGrooming").GetBoolean());
        var stale = await _client.PostAsJsonAsync($"/api/specs/{spec.Id}/groom/retry?project=p", new { expectedRunId = run.Id + 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var foreign = await _client.PostAsJsonAsync($"/api/specs/{spec.Id}/groom/retry?project=other", new { expectedRunId = run.Id });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var queued = await _client.PostAsJsonAsync($"/api/specs/{spec.Id}/groom/retry?project=p", new { expectedRunId = run.Id });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Single(await _runs.ListQueuedAsync());
        Assert.Empty(await _issues.ListAsync(new IssueFilter()));
        var duplicate = await _client.PostAsJsonAsync($"/api/specs/{spec.Id}/groom/retry?project=p", new { expectedRunId = run.Id });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task GenericPatchCannotInventGroomedCompletion()
    {
        var spec = await _specs.CreateAsync(new NewSpec("p", "Spec", "Body"));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming);
        var response = await _client.PatchAsJsonAsync($"/api/specs/{spec.Id}?project=p", new { op = "set_status", status = "Groomed" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SpecStatus.Grooming, (await _specs.GetAsync(spec.Id))!.Status);
    }
}

