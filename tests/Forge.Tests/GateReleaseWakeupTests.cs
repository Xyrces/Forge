using Forge.Agents;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Dashboard;
using Forge.Messaging;
using Forge.Orchestrator;
using Forge.Orchestrator.Consumers;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests;

public sealed class GateReleaseWakeupTests : IAsyncLifetime
{
    private readonly string _dir = TempRoot.Instance.NewDirectory("gate-wakeup");
    private IssueStore _issues = null!;
    private ProjectStore _projects = null!;
    private MemoryStore _memory = null!;
    private StageGates _gates = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly InMemoryTransport _transport = new();
    private RecordingPublisher _publisher = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.db");
        _issues = new IssueStore(path);
        _projects = new ProjectStore(_issues);
        await _projects.UpsertAsync(new NewProject("forge", "Forge", "https://example.test/forge", "main"));
        await _projects.UpsertAsync(new NewProject("other", "Other", "https://example.test/other", "main"));
        _memory = new MemoryStore(path);
        _gates = new StageGates(_memory);
        _publisher = new RecordingPublisher(_gates,
            new TalariaEventPublisher(_transport, NullLogger<TalariaEventPublisher>.Instance));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        GateEndpoints.MapGateEndpoints(_app, _gates, NullLogger.Instance, _publisher, _projects);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_app.GetPort()}/") };
    }

    [Theory]
    [InlineData("design", SweepKind.Design)]
    [InlineData("groom", SweepKind.Groom)]
    [InlineData("sprint", SweepKind.Assemble)]
    [InlineData("merge", SweepKind.Watch)]
    [InlineData("GROOM", SweepKind.Groom)]
    public async Task Release_CommitsBeforePublishingCorrectHintForEveryProject(string stage, SweepKind expected)
    {
        await _gates.HoldAsync(stage.ToLowerInvariant());
        (await _client.PostAsync($"api/gates/{stage}/release", null)).EnsureSuccessStatusCode();
        Assert.False(await _gates.IsHeldAsync(stage.ToLowerInvariant()));
        Assert.Equal(2, _publisher.Events.Count);
        Assert.All(_publisher.Events, evt => Assert.Equal(expected, evt.Kind));
        Assert.Equal(new[] { "forge", "other" }, _publisher.Events.Select(e => e.ProjectId).OrderBy(id => id));
        Assert.Equal(2, _publisher.Events.Select(e => e.MessageId).Distinct().Count());
        Assert.All(_publisher.PersistedOpenAtPublish, Assert.True);
    }

    [Theory]
    [InlineData("design", SweepKind.Design)]
    [InlineData("groom", SweepKind.Groom)]
    [InlineData("sprint", SweepKind.Assemble)]
    public async Task Release_ReachesOrdinaryConsumerAndWakesSchedulerWithoutBackstop(string stage, SweepKind kind)
    {
        var wakeups = SchedulerWakeups.Create();
        var sweeps = new WatchSweepService(new NeverRunner(), null, null, new ModelRateLimitTracker(), null, null,
            new InMemoryDashboardEventBus(), NullLoggerFactory.Instance, NullLogger<WatchSweepService>.Instance);
        var consumer = new SweepTickConsumer(_transport, new NeverBundleFactory(), _projects, wakeups, sweeps,
            NullLogger<SweepTickConsumer>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await consumer.StartAsync(deadline.Token);
        try
        {
            await _gates.HoldAsync(stage);
            (await _client.PostAsync($"api/gates/{stage}/release", null)).EnsureSuccessStatusCode();
            await wakeups.For(kind).WaitAsync(deadline.Token);
            Assert.False(await _gates.IsHeldAsync(stage));
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task HoldAndUnknownRelease_DoNotWakePipeline()
    {
        (await _client.PostAsync("api/gates/groom/hold", null)).EnsureSuccessStatusCode();
        Assert.True(await _gates.IsHeldAsync("groom"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await _client.PostAsync("api/gates/unknown/release", null)).StatusCode);
        Assert.Empty(_publisher.Events);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotUndoPersistedRelease()
    {
        await _gates.HoldAsync("groom");
        _publisher.Fail = true;
        (await _client.PostAsync("api/gates/groom/release", null)).EnsureSuccessStatusCode();
        Assert.False(await _gates.IsHeldAsync("groom"));
    }

    [Fact]
    public async Task OneProjectNotificationFailure_DoesNotSuppressOtherProjects()
    {
        _publisher.FailProject = "forge";
        (await _client.PostAsync("api/gates/groom/release", null)).EnsureSuccessStatusCode();
        Assert.Equal("other", Assert.Single(_publisher.Events).ProjectId);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _projects.DisposeAsync();
        _memory.Dispose();
        await _issues.DisposeAsync();
    }

    private sealed class RecordingPublisher(StageGates gates, IEventPublisher inner) : IEventPublisher
    {
        public List<SweepTick> Events { get; } = [];
        public List<bool> PersistedOpenAtPublish { get; } = [];
        public bool Fail { get; set; }
        public string? FailProject { get; set; }
        public async Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        {
            if (Fail) throw new IOException("notification unavailable");
            var tick = Assert.IsType<SweepTick>(evt);
            if (tick.ProjectId == FailProject) throw new IOException("project notification unavailable");
            var stage = tick.Kind switch { SweepKind.Assemble => "sprint", SweepKind.Watch => "merge", _ => tick.Kind.ToString().ToLowerInvariant() };
            PersistedOpenAtPublish.Add(!await gates.IsHeldAsync(stage, ct));
            Events.Add(tick);
            await inner.PublishAsync(evt, ct);
        }
    }

    private sealed class NeverRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => throw new InvalidOperationException("Scheduler notification must not invoke an agent directly.");
    }
    private sealed class NeverBundleFactory : IProjectDispatchBundleFactory
    {
        public ProjectDispatchBundle Build(Forge.Configuration.ProjectOptions project)
            => throw new InvalidOperationException("Planning wakeup must not dispatch work directly.");
    }
}
