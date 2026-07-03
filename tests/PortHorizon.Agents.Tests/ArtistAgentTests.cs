using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Meshy;
using PortHorizon.Agents.Orchestrator;
using Xunit;
using Xunit.Abstractions;

namespace PortHorizon.Agents.Tests;

/// <summary>
/// ArtistAgent integration tests. Mirror the DesignerAgent test
/// pattern (scripted chat client + real stores). The Meshy HTTP
/// client is stubbed via a <see cref="StubHttpHandler"/> so the
/// tests don't hit the network.
/// </summary>
public class ArtistAgentTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ArtOutputStore _artOutputs;
    private readonly ArtistRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly InMemoryDashboardEventBus _events;

    public ArtistAgentTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-artist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _specs = new SpecStore(_issues);
        _designArtifacts = new DesignArtifactStore(Path.Combine(_workDir, "issues.db"));
        _artOutputs = new ArtOutputStore(Path.Combine(_workDir, "issues.db"));
        _runs = new ArtistRunStore(Path.Combine(_workDir, "issues.db"));
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        _issues.Dispose();
        _specs.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private ArtistAgent NewAgent(IChatClient client, MeshyClient meshy)
    {
        var factory = new SingleClientFactory(client);
        var config = new LlmConfig(new ProviderConfig("stub", "", null, null, "stub-model"));
        var roles = new RoleAgentRegistry();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(_out)));
        return new ArtistAgent(
            _specs, _designArtifacts, _artOutputs, _runs, _memory, meshy, factory, config, roles, _events,
            loggerFactory.CreateLogger<ArtistAgent>());
    }

    private async Task<SpecRecord> CreateSpecAsync(SpecStatus status = SpecStatus.Designed)
    {
        var spec = await _specs.CreateAsync(new NewSpec("P", "Inventory HUD",
            "## Summary\nInventory HUD visual.\n## Touches\n- PortHorizon.Core\n## Dependencies\n- none\n## Acceptance criteria\n- [ ] x"));
        // Walk the legal transitions to land in the requested
        // status. The SpecStatusTransitions table is enforced
        // for every SetStatusAsync, so we can't jump directly.
        // Draft -> ReadyForDesign -> Designed is the canonical
        // path. Draft -> Approved is the operator's "skip
        // design" fast-path.
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
        if (status == SpecStatus.ReadyForDesign) return (await _specs.GetAsync(spec.Id))!;
        await _specs.SetStatusAsync(spec.Id, SpecStatus.Designed);
        if (status == SpecStatus.Designed) return (await _specs.GetAsync(spec.Id))!;
        if (status == SpecStatus.AssetReady) { await _specs.SetStatusAsync(spec.Id, SpecStatus.AssetReady); return (await _specs.GetAsync(spec.Id))!; }
        if (status == SpecStatus.NeedsRevision) { await _specs.SetStatusAsync(spec.Id, SpecStatus.NeedsRevision); return (await _specs.GetAsync(spec.Id))!; }
        throw new ArgumentException($"test helper doesn't support status {status}");
    }

    private sealed class SingleClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleClientFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _out;
        public TestLoggerProvider(ITestOutputHelper o) { _out = o; }
        public ILogger CreateLogger(string categoryName) => new TestLogger(_out, categoryName);
        public void Dispose() { }
        private sealed class TestLogger : ILogger
        {
            private readonly ITestOutputHelper _o;
            private readonly string _cat;
            public TestLogger(ITestOutputHelper o, string cat) { _o = o; _cat = cat; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Nop();
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try { _o.WriteLine($"[{logLevel}] {_cat}: {formatter(state, exception)}"); } catch { }
            }
            private sealed class Nop : IDisposable { public void Dispose() { } }
        }
    }

    /// <summary>HttpMessageHandler that returns canned responses keyed by URL path.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public Dictionary<string, HttpResponseMessage> Responses { get; } = new();
        public int CallCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (Responses.TryGetValue($"{request.Method} {path}", out var resp))
            {
                return Task.FromResult(resp);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unhandled {request.Method} {path}"),
            });
        }
    }

    private MeshyClient NewMeshy(StubHttpHandler handler, string? artOutputRoot = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MeshyOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.test",
            PollIntervalSeconds = 1,
            MaxWaitSeconds = 5,
        });
        return new MeshyClient(handler, options,
            NullLogger<MeshyClient>.Instance,
            artOutputRoot ?? Path.Combine(_workDir, "art-output"));
    }

    [Fact]
    public async Task LlmsCallsDbSetSpecStatusAssetReady_AfterSavingArtOutput()
    {
        // Set up Meshy stub: text-to-3d submit returns a task id,
        // poll returns SUCCEEDED with a glb URL, glb download returns
        // a tiny glb-shaped body.
        var handler = new StubHttpHandler();
        handler.Responses["POST /openapi/v2/text-to-3d"] = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result = "task-001" })),
        };
        handler.Responses["GET /openapi/v2/text-to-3d/task-001"] = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "task-001", status = "SUCCEEDED",
                model_urls = new { glb = "https://signed.example/test.glb" },
            })),
        };
        // The .glb download uses a different HttpClient (no bearer);
        // we need a handler that responds to the signed URL too. Add
        // a wildcard by recording a response on the SAME handler
        // (it'll handle any host). MeshyClient uses a separate
        // HttpClient without a handler though, so we need to stub
        // the signed-URL fetch differently. For this test, the
        // simplest path is: write the .glb bytes via a custom
        // HttpMessageHandler that catches the secondary client.
        // Easiest: have the .glb body come from a data URL? Not
        // supported by Meshy. Instead, use a HttpClient that uses
        // THIS handler too.
        // Since MeshyClient creates `new HttpClient()` for the glb
        // download (no handler), we can't intercept it. We can only
        // assert that db_submit_meshy_job returned the glb URL
        // envelope; the download step would 404 in tests.
        // Simplest: skip the download by checking only the LLM
        // sees the envelope. We won't assert on the .glb file.
        var meshy = NewMeshy(handler);

        var spec = await CreateSpecAsync();
        // Three-step client: submit meshy -> save art -> set status.
        var client = new ThreeStepClient(
            ("db_submit_meshy_job", new Dictionary<string, object?>
            {
                ["mode"] = "text-to-3d",
                ["input"] = "a small wooden crate",
            }, JsonSerializer.Serialize(new
            {
                TaskId = "task-001", Mode = "TextTo3d",
                Status = "SUCCEEDED", GlbUrl = "https://signed.example/test.glb",
            })),
            ("db_save_art_output", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["kind"] = "mesh",
                ["title"] = "Crate",
                // The agent is expected to pass the JSON envelope
                // from db_submit_meshy_job as the body. The save
                // step tries to download the .glb; that fails
                // in tests (no network) so we save a direct path
                // instead by passing a non-JSON body. Skip the
                // glb download by using a body that's a plain
                // relative path.
                ["body"] = "spec-fake/crate.glb",
                ["bodyKind"] = "glb",
            }, "art-test-001"),
            ("db_set_spec_status", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["status"] = "AssetReady",
            }, "ok"),
            finalText: "Produced one mesh.");

        var agent = NewAgent(client, meshy);
        var result = await agent.ArtSpecAsync(spec.Id, ArtistTriggerKind.Manual);

        Assert.True(result.Success);
        Assert.Equal(SpecStatus.AssetReady, result.NewSpecStatus);
        Assert.Single(result.ArtOutputIds);
        // The run log records the produced art_output id.
        var run = (await _runs.ListAsync(spec.Id)).Single();
        Assert.Equal(ArtistRunStatus.Succeeded, run.Status);
        Assert.Equal(SpecStatus.AssetReady, run.NewSpecStatus);
        Assert.NotNull(run.ArtOutputIds);
        Assert.Single(run.ArtOutputIds!);
        // Note: the LLM-driven test passes a plain relative path
        // as the body to db_save_art_output (not a JSON envelope
        // with a Meshy task id), so the run log doesn't reconstruct
        // the meshy_tasks list. The run log rebuilds meshy_tasks
        // from art_output.references_json in production. The
        // dedicated MeshyClient tests cover that path; the Agent
        // tests cover the orchestration.
        Assert.Empty(run.MeshyTasks ?? new List<MeshyTaskRecord>());
    }

    [Fact]
    public async Task LlmsCallsDbSetSpecStatusNeedsRevision_ReportsVisualProblem()
    {
        var handler = new StubHttpHandler();
        var meshy = NewMeshy(handler);

        var spec = await CreateSpecAsync();
        var client = new TwoStepClient(
            null,
            ("db_set_spec_status", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["status"] = "NeedsRevision",
            }, "ok"),
            finalText: "Wireframe is too vague to mesh.");

        var agent = NewAgent(client, meshy);
        var result = await agent.ArtSpecAsync(spec.Id, ArtistTriggerKind.Manual);

        Assert.True(result.Success);
        Assert.Equal(SpecStatus.NeedsRevision, result.NewSpecStatus);
        Assert.Empty(result.ArtOutputIds);
    }

    [Fact]
    public async Task LlmDoesNotCallDbSetSpecStatus_FailsRun()
    {
        var handler = new StubHttpHandler();
        var meshy = NewMeshy(handler);

        var spec = await CreateSpecAsync();
        var client = new ArtistScriptedToolCallClient(
            "db_submit_meshy_job",
            new Dictionary<string, object?>
            {
                ["mode"] = "text-to-3d",
                ["input"] = "x",
            },
            JsonSerializer.Serialize(new
            {
                TaskId = "t", Mode = "TextTo3d", Status = "SUCCEEDED", GlbUrl = (string?)null,
            }));
        var agent = NewAgent(client, meshy);
        var result = await agent.ArtSpecAsync(spec.Id, ArtistTriggerKind.Manual);

        Assert.False(result.Success);
        Assert.Contains("without committing a spec status", result.Error);
        var fresh = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.Designed, fresh.Status);  // unchanged
        var run = (await _runs.ListAsync(spec.Id)).Single();
        Assert.Equal(ArtistRunStatus.LlmFailed, run.Status);
    }

    [Fact]
    public async Task SpecNotFound_RecordsLlmFailed()
    {
        var handler = new StubHttpHandler();
        var meshy = NewMeshy(handler);
        var client = new ArtistScriptedToolCallClient("db_get_spec",
            new Dictionary<string, object?> { ["specId"] = "missing" }, "{}");
        var agent = NewAgent(client, meshy);
        var result = await agent.ArtSpecAsync("missing", ArtistTriggerKind.Manual);

        Assert.False(result.Success);
        Assert.Contains("Spec not found", result.Error);
    }
}

/// <summary>Local copy of DesignerAgentTests.ScriptedToolCallClient (which
/// is private). Drives the LLM to call a single AIFunction on the
/// first turn then return plain text on the second.</summary>
internal sealed class ArtistScriptedToolCallClient : IChatClient
{
    private readonly string _toolName;
    private readonly Dictionary<string, object?> _args;
    private readonly string _finalText;
    public int CallCount;

    public ArtistScriptedToolCallClient(string toolName, Dictionary<string, object?> args, string finalText)
    {
        _toolName = toolName;
        _args = args;
        _finalText = finalText;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (CallCount == 1)
        {
            var call = new FunctionCallContent("c1", _toolName, _args);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _finalText)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Three-step scripted client. Steps 1-3 fire on turns 1-3 in
/// order; after the last, returns plain text. Each step is
/// optional (pass null to skip that turn).
/// </summary>
internal sealed class ThreeStepClient : IChatClient
{
    private readonly (string Name, Dictionary<string, object?> Args, string Result)? _step1;
    private readonly (string Name, Dictionary<string, object?> Args, string Result)? _step2;
    private readonly (string Name, Dictionary<string, object?> Args, string Result) _step3;
    private readonly string _finalText;
    public int CallCount;

    public ThreeStepClient(
        (string, Dictionary<string, object?>, string)? step1,
        (string, Dictionary<string, object?>, string)? step2,
        (string, Dictionary<string, object?>, string) step3,
        string finalText)
    {
        _step1 = step1;
        _step2 = step2;
        _step3 = step3;
        _finalText = finalText;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (CallCount == 1 && _step1 is not null) return ToolTurn(_step1.Value, "c1");
        if (CallCount == 2 && _step2 is not null) return ToolTurn(_step2.Value, "c2");
        if (CallCount == 3 || (CallCount == 1 && _step1 is null) || (CallCount == 2 && _step2 is null))
            return ToolTurn(_step3, "c3");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _finalText)));
    }

    private Task<ChatResponse> ToolTurn((string Name, Dictionary<string, object?> Args, string Result) s, string callId)
    {
        var call = new FunctionCallContent(callId, s.Name, s.Args);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
