using System.Data.Common;
using Forge.Agents;
using Forge.Core;
using Forge.Core.Db;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class GroomingFinalizationTests
{
    [Fact]
    public async Task FailedAcknowledgementRetriesWhileHeld_WithoutAnotherModelCall()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("groom-ack"), "p");
        var specs = new SpecStore(issues);
        var spec = await specs.CreateAsync(new NewSpec("p", "Spec", "Body"));
        await specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        var fault = new FailOnceFactory(issues.Db);
        var runs = new IssueGroomerRunStore(fault);
        var client = new InterruptedClient(() => fault.FailNext = true);
        var events = new InMemoryDashboardEventBus();
        var factory = new GroomerAgentFactory(issues, specs, events, new ScriptingChatClientFactory(client),
            new LlmConfig(new ProviderConfig("test", "", null, null, "test")), NullLoggerFactory.Instance);
        var gates = new StageGates(new MemoryStore(issues.Db));
        var scheduler = new ScheduledGroomer(specs, factory, runs, events,
            NullLogger<ScheduledGroomer>.Instance, gates: gates);
        await scheduler.TickAsync(default);
        Assert.Equal(1, client.Calls);
        Assert.Equal(GroomerRunStatus.Started, Assert.Single(await runs.ListAsync()).Status);
        await gates.HoldAsync(StageGates.Groom);
        await scheduler.TickAsync(default);
        Assert.Equal(GroomerRunStatus.NeedsReview, Assert.Single(await runs.ListAsync()).Status);
        Assert.Equal(1, client.Calls);
        await gates.ReleaseAsync(StageGates.Groom);
        await scheduler.TickAsync(default);
        Assert.Equal(1, client.Calls);
        Assert.Single(await runs.ListAsync());
    }

    private sealed class InterruptedClient(Action interrupt)
        : DelegatingChatClient(new MultiToolCallingChatClient([], "unused"))
    {
        public int Calls { get; private set; }
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            interrupt();
            throw new OperationCanceledException("Simulated uncertain model interruption.");
        }
    }

    private sealed class FailOnceFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public bool FailNext { get; set; }
        public ForgeDbProvider Provider => inner.Provider;
        public ISqlDialect Dialect => inner.Dialect;
        public ForgeSchemaProfile Profile => inner.Profile;
        public string ConnectionString => inner.ConnectionString;
        public string Qualifier => inner.Qualifier;
        public DbConnection Open() => inner.Open();
        public Task<DbConnection> OpenAsync(CancellationToken ct = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Simulated transient acknowledgement outage.");
            }
            return inner.OpenAsync(ct);
        }
    }
}

