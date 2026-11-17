using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class GroomingFreshnessTests
{
    [Theory]
    [InlineData("queued")]
    [InlineData("scheduled")]
    [InlineData("adhoc")]
    public async Task RefreshFailureLeavesPaidAttemptUnclaimed_AndRetryRefreshesBeforeModel(string kind)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("groom-freshness"), "p");
        var specs = new SpecStore(issues);
        var runs = new IssueGroomerRunStore(issues.Db);
        SpecRecord? spec = null;
        if (kind == "adhoc") await issues.CreateAsync(new NewIssue("task", "Pending task"));
        else
        {
            spec = await specs.CreateAsync(new NewSpec("p", "Spec", "Body"));
            await specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
            if (kind == "queued") await runs.QueueAsync(spec.Id, false);
        }
        var refreshed = false;
        var refreshCalls = 0;
        var root = TempRoot.Instance.NewDirectory("groom-fresh-tree");
        Directory.CreateDirectory(root);
        var client = new CountingClient(() => Assert.True(refreshed));
        var events = new InMemoryDashboardEventBus();
        var factory = new GroomerAgentFactory(issues, specs, events, new ScriptingChatClientFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "test")), NullLoggerFactory.Instance,
            projectRootLookup: project =>
            {
                Assert.Equal("p", project);
                Assert.True(refreshed);
                return root;
            },
            refreshContext: (project, ct) =>
            {
                Assert.Equal("p", project);
                if (++refreshCalls == 1) throw new InvalidOperationException("Fetch temporarily unavailable.");
                refreshed = true;
                return Task.CompletedTask;
            });
        var scheduler = new ScheduledGroomer(specs, factory, runs, events,
            NullLogger<ScheduledGroomer>.Instance, issues: issues);
        await scheduler.TickAsync(default);
        Assert.Equal(0, client.Calls);
        if (kind == "queued") Assert.Equal(GroomerRunStatus.Queued, Assert.Single(await runs.ListAsync()).Status);
        else Assert.Empty(await runs.ListAsync());
        if (spec is not null) Assert.Equal(SpecStatus.Approved, (await specs.GetAsync(spec.Id))!.Status);
        await scheduler.TickAsync(default);
        Assert.True(refreshed);
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, refreshCalls);
    }

    private sealed class CountingClient(Action assertFresh)
        : DelegatingChatClient(new MultiToolCallingChatClient([], "unused"))
    {
        public int Calls { get; private set; }
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            assertFresh();
            throw new OperationCanceledException("Stop after proving paid-call admission ordering.");
        }
    }
}
