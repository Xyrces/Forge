using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Meshy;
using Forge.Orchestrator;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests.Integration;

/// <summary>
/// ArtistScheduler tests: the scheduler picks up Designed specs
/// and runs the Artist on each. Mirrors DesignerSchedulerTests
/// but uses a stubbed Meshy HTTP handler.
/// </summary>
public class ArtistSchedulerTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly MemoryStore _memory;
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ArtOutputStore _artOutputs;
    private readonly ArtistRunStore _artistRuns;
    private readonly InMemoryDashboardEventBus _events;

    public ArtistSchedulerTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = TempRoot.Instance.NewDirectory("ascheduler");
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
        _memory = new MemoryStore(_dbPath);
        _designArtifacts = new DesignArtifactStore(_dbPath);
        _artOutputs = new ArtOutputStore(_dbPath);
        _artistRuns = new ArtistRunStore(_dbPath);
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        _issues.Dispose();
        _specs.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private sealed class InlineFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public InlineFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, Queue<HttpResponseMessage>> Responses { get; } = new();
        public bool AllowAnyGlbDownload;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            if (AllowAnyGlbDownload && request.RequestUri.Host == "signed.example")
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("glb-bytes"))
                });
            }
            if (Responses.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unhandled {key}"),
            });
        }
    }

    private MeshyClient NewMeshy()
    {
        var handler = new StubHandler { AllowAnyGlbDownload = true };
        var options = Microsoft.Extensions.Options.Options.Create(new MeshyOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.test",
            PollIntervalSeconds = 1,
            MaxWaitSeconds = 5,
        });
        return new MeshyClient(handler, options,
            NullLogger<MeshyClient>.Instance,
            artOutputRoot: Path.Combine(_workDir, "art-output"));
    }

    /// <summary>Artist script: turn 1 = submit_meshy, turn 2 = save_art,
    /// turn 3 = set_status(AssetReady).</summary>
    private sealed class ArtistSuccessScript : IChatClient
    {
        private readonly string _specId;
        public int CallCount;
        public ArtistSuccessScript(string specId) { _specId = specId; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                var call = new FunctionCallContent("c1", "db_submit_meshy_job",
                    new Dictionary<string, object?>
                    {
                        ["mode"] = "text-to-3d",
                        ["input"] = "a crate",
                    });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
            }
            if (CallCount == 2)
            {
                // The save_art step takes the JSON envelope from
                // step 1 as the body; the agent passes it verbatim.
                // In our tests we use a relative path body instead
                // (no .glb download).
                var call = new FunctionCallContent("c2", "db_save_art_output",
                    new Dictionary<string, object?>
                    {
                        ["specId"] = _specId,
                        ["kind"] = "mesh",
                        ["title"] = "Crate",
                        ["body"] = $"{_specId}/crate.glb",
                        ["bodyKind"] = "glb",
                    });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
            }
            if (CallCount == 3)
            {
                var call = new FunctionCallContent("c3", "db_set_spec_status",
                    new Dictionary<string, object?>
                    {
                        ["specId"] = _specId,
                        ["status"] = "AssetReady",
                    });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Always returns plain text — agent will not call db_set_spec_status
    /// and the run will be LlmFailed.</summary>
    private sealed class ArtistFailureScript : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I refuse.")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private async Task<SpecRecord> CreateDesignedSpecAsync(string title)
    {
        var spec = await _specs.CreateAsync(new NewSpec("PortHorizon", title,
            "## Summary\nx\n\n## Acceptance criteria\n- [ ] a\n\n## Touches\n- PortHorizon.Client\n\n## Dependencies\n- none\n"));
        // Walk the chain: Draft -> ReadyForDesign -> Designed.
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Designed);
        return (await _specs.GetAsync(spec.Id))!;
    }

    private ArtistAgentFactory NewArtistFactory(IChatClient client, MeshyClient meshy) => new(
        _specs, _designArtifacts, _artOutputs, _artistRuns, _memory, meshy,
        new InlineFactory(client),
        new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
        new RoleAgentRegistry(),
        _events,
        NullLoggerFactory.Instance);

    [Fact]
    public async Task Tick_FindsDesignedSpecs_AndRunsThem()
    {
        var spec = await CreateDesignedSpecAsync("Inventory HUD");
        var meshy = NewMeshy();
        // The ArtistSuccessScript passes a relative path body to
        // db_save_art_output (not a JSON envelope), so the .glb
        // download step is bypassed. No handler wiring needed.
        var factory = NewArtistFactory(new ArtistSuccessScript(spec.Id), meshy);
        var scheduler = new ArtistScheduler(
            _specs, factory, _artistRuns, _events,
            NullLogger<ArtistScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler.TickAsync(default);

        var after = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.AssetReady, after.Status);
        var arts = await _artOutputs.ListBySpecAsync(spec.Id);
        Assert.Single(arts);
        Assert.Equal(ArtOutputKind.Mesh, arts[0].Kind);
        var runs = await _artistRuns.ListAsync(specId: spec.Id, limit: 1);
        Assert.Single(runs);
        Assert.Equal(ArtistRunStatus.Succeeded, runs[0].Status);
        Assert.Equal(SpecStatus.AssetReady, runs[0].NewSpecStatus);
    }

    [Fact]
    public async Task Tick_SkipsRecentlySucceededSpec()
    {
        var spec = await CreateDesignedSpecAsync("Test A");
        var meshy = NewMeshy();
        var factory = NewArtistFactory(new ArtistSuccessScript(spec.Id), meshy);
        var scheduler = new ArtistScheduler(_specs, factory, _artistRuns, _events,
            NullLogger<ArtistScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler.TickAsync(default);

        var fresh = new ArtistSuccessScript(spec.Id);
        var factory2 = NewArtistFactory(fresh, meshy);
        var scheduler2 = new ArtistScheduler(_specs, factory2, _artistRuns, _events,
            NullLogger<ArtistScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler2.TickAsync(default);
        Assert.Equal(0, fresh.CallCount);
    }

    [Fact]
    public async Task Tick_ReRunsSpecWhoseLastArtFailed()
    {
        var spec = await CreateDesignedSpecAsync("Test B");
        var meshy = NewMeshy();
        var failureFactory = NewArtistFactory(new ArtistFailureScript(), meshy);
        var scheduler1 = new ArtistScheduler(_specs, failureFactory, _artistRuns, _events,
            NullLogger<ArtistScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler1.TickAsync(default);

        var firstRuns = await _artistRuns.ListAsync(specId: spec.Id, limit: 1);
        Assert.Single(firstRuns);
        Assert.Equal(ArtistRunStatus.LlmFailed, firstRuns[0].Status);

        var successFactory = NewArtistFactory(new ArtistSuccessScript(spec.Id), meshy);
        var scheduler2 = new ArtistScheduler(_specs, successFactory, _artistRuns, _events,
            NullLogger<ArtistScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        scheduler2.FailedRetryCooldown = TimeSpan.Zero;
        await scheduler2.TickAsync(default);

        var after = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.AssetReady, after.Status);
        var allRuns = await _artistRuns.ListAsync(specId: spec.Id, limit: 10);
        Assert.Equal(2, allRuns.Count);
        Assert.Equal(ArtistRunStatus.Succeeded, allRuns[0].Status);
        Assert.Equal(ArtistRunStatus.LlmFailed, allRuns[1].Status);
    }

    [Fact]
    public async Task Tick_OnlyArtsDesignedSpecs_NotOtherStatuses()
    {
        // Specs in Draft, NeedsRevision, AssetReady should NOT
        // be picked up. Only Designed.
        var designed = await CreateDesignedSpecAsync("Designed");
        // assetReady walks forward from Designed (legal).
        var assetReady = await CreateDesignedSpecAsync("AssetReady");
        await _specs.SetStatusAsync(assetReady.Id, SpecStatus.AssetReady);
        // needsRev walks via the legal chain: ReadyForDesign -> Designed -> NeedsRevision.
        var needsRev = await CreateDesignedSpecAsync("NeedsRev");
        await _specs.SetStatusAsync(needsRev.Id, SpecStatus.NeedsRevision);
        // draft stays in Draft.
        var draft = await _specs.CreateAsync(new NewSpec("PortHorizon", "Draft",
            "## Summary\nx\n\n## Acceptance criteria\n- [ ] a\n\n## Touches\n- PortHorizon.Client\n\n## Dependencies\n- none\n"));

        var meshy = NewMeshy();
        var factory = NewArtistFactory(new ArtistSuccessScript(designed.Id), meshy);
        var scheduler = new ArtistScheduler(_specs, factory, _artistRuns, _events,
            NullLogger<ArtistScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler.TickAsync(default);

        var allRuns = await _artistRuns.ListAsync(limit: 100);
        Assert.Single(allRuns);
        Assert.Equal(designed.Id, allRuns[0].SpecId);
    }
}

