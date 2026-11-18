using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Meshy;
using Forge.Orchestrator;
using Xunit;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using Forge.Tests.Integration;

namespace Forge.Tests;

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
        _workDir = TempRoot.Instance.NewDirectory("artist");
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
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => _client;
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
    public async Task SecondaryArtistWritesAndServesOwnedFilesAndMaterializedHistory()
    {
        await using var secondary = new IssueStore(Path.Combine(_workDir, "second.db"), "second");
        var owner = new SpecStore(secondary);
        var spec = await owner.CreateAsync(new("stale-project", "Owned asset", "Create a mesh"));
        await owner.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
        await owner.SetStatusAsync(spec.Id, SpecStatus.Designed);
        var routing = new ProjectRoutingSpecStore(_specs,
            id => id == "second" ? owner : null, () => [_specs, owner]);
        var handler = new StubHttpHandler();
        handler.Responses["GET /mesh.glb"] = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x67, 0x6c, 0x54, 0x46, 1, 2, 3]),
        };
        var meshy = NewMeshy(handler);
        var ownerRoot = Path.Combine(_workDir, "owned-art");
        var client = new TwoStepClient(
            ("db_save_art_output", new() { ["specId"] = spec.Id, ["kind"] = "mesh", ["title"] = "Mesh",
                ["body"] = "https://assets.test/mesh.glb", ["bodyKind"] = "glb" }, "saved"),
            ("db_set_spec_status", new() { ["specId"] = spec.Id, ["status"] = "AssetReady" }, "ok"), "Done");
        var factory = new ArtistAgentFactory(routing, _designArtifacts, _artOutputs, _runs, _memory, meshy,
            new SingleClientFactory(client), new LlmConfig(new ProviderConfig("stub", "", null, null, "stub-model")),
            new RoleAgentRegistry(), _events, NullLoggerFactory.Instance,
            artOutputRoot: id => id == "second" ? ownerRoot : throw new InvalidOperationException("Wrong physical owner"));
        var result = await factory.Create().ArtSpecAsync(spec.Id, ArtistTriggerKind.Manual);
        Assert.True(result.Success, result.Error);
        var created = Assert.Single(await new ArtOutputStore(secondary.Db).ListBySpecAsync(spec.Id));
        Assert.Empty(await _artOutputs.ListBySpecAsync(spec.Id));
        var expected = new byte[] { 0x67, 0x6c, 0x54, 0x46, 1, 2, 3 };
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(ownerRoot, created.Body)));
        Assert.False(File.Exists(Path.Combine(meshy.ArtOutputRoot, created.Body)));
        var historical = await _artOutputs.CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "History", spec.Id + "/old.glb", "glb"));
        Directory.CreateDirectory(Path.Combine(meshy.ArtOutputRoot, spec.Id));
        await File.WriteAllBytesAsync(Path.Combine(meshy.ArtOutputRoot, historical.Body), expected);
        await new OwnedArtifactHistoryStore(secondary.Db).ImportAsync(new("primary", _issues.Db, meshy.ArtOutputRoot), owner);
        var foreign = await _artOutputs.CreateAsync(new("foreign-spec", ArtOutputKind.Mesh, "Foreign", "foreign-spec/x.glb", "glb"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ArtistEndpoints.MapArtistEndpoints(app, routing, factory, _runs, _artOutputs, meshy, NullLogger.Instance);
        DesignArtEndpoints.MapDesignArtEndpoints(app, _designArtifacts, _artOutputs, null, _runs, NullLogger.Instance, routing);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        var gallery = await http.GetFromJsonAsync<JsonElement>("/api/art-output?projectId=second");
        Assert.Equal(2, gallery.GetArrayLength());
        foreach (var entry in gallery.EnumerateArray())
            Assert.Equal(expected, await http.GetByteArrayAsync(entry.GetProperty("fileUrl").GetString()));
        var rows = await http.GetFromJsonAsync<JsonElement>($"/api/specs/{spec.Id}/art-output");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(expected, await http.GetByteArrayAsync($"/api/art-output/{historical.Id}/file"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await http.GetAsync($"/api/art-output/{foreign.Id}/file?specId={spec.Id}")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task LegacyFileBookmarkUsesRetainedHistoryAfterPrimaryRemovalWithoutExposingCurrentOtherProjectArt()
    {
        await using var next = new IssueStore(Path.Combine(_workDir, "next.db"), "next");
        await using var ownerIssues = new IssueStore(Path.Combine(_workDir, "owner.db"), "owner");
        var owner = new SpecStore(ownerIssues);
        var spec = await owner.CreateAsync(new("owner", "Owner", "body"));
        var meshy = NewMeshy(new StubHttpHandler());
        Directory.CreateDirectory(Path.Combine(meshy.ArtOutputRoot, spec.Id));
        var historical = await _artOutputs.CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Old link", spec.Id + "/old.glb", "glb"));
        await File.WriteAllTextAsync(Path.Combine(meshy.ArtOutputRoot, historical.Body), "retained bytes");
        await new OwnedArtifactHistoryStore(ownerIssues.Db).ImportAsync(new("removed-writer", _issues.Db, meshy.ArtOutputRoot), owner);
        var local = await new ArtOutputStore(ownerIssues.Db).CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "Current", spec.Id + "/current.glb", "glb"));
        await File.WriteAllTextAsync(Path.Combine(meshy.ArtOutputRoot, local.Body), "current bytes");
        var registry = new ProjectStore(_issues);
        await registry.UpsertAsync(new("next", "Next", "https://example.invalid/next.git"));
        await registry.UpsertAsync(new("owner", "Owner", "https://example.invalid/owner.git"));
        await using var projects = new Forge.Projects.ProjectContextFactory(registry, _workDir,
            dbResolver: (id, _) => id == "owner" ? ownerIssues.Db : throw new InvalidOperationException("Unrelated store opened"));
        _ = projects.Find("owner");
        var nextSpecs = new SpecStore(next);
        var routing = new ProjectRoutingSpecStore(nextSpecs, _ => null,
            () => throw new InvalidOperationException("Legacy link must not initialize registered stores"),
            () => projects.GetInitializedHistoryStores(nextSpecs));
        await registry.UpsertAsync(new("unready", "Unready", "https://example.invalid/unready.git"));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ArtistEndpoints.MapArtistEndpoints(app, routing, null, _runs, new ArtOutputStore(next.Db), meshy, NullLogger.Instance);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.GetPort()}") };
        Assert.Equal("retained bytes", await http.GetStringAsync($"/api/art-output/{historical.Id}/file"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await http.GetAsync($"/api/art-output/{local.Id}/file")).StatusCode);
        await new OwnedArtifactHistoryStore(ownerIssues.Db).ImportAsync(
            new("conflicting-writer", _issues.Db, meshy.ArtOutputRoot), owner);
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await http.GetAsync($"/api/art-output/{historical.Id}/file")).StatusCode);
        await registry.DeleteAsync("owner");
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await http.GetAsync($"/api/art-output/{historical.Id}/file")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_workDir, "projects", "unready")));
        await app.StopAsync();
    }

    [Fact]
    public async Task FreshSingleOwnerIncludesItsTypedStoreInLegacyHistoryAndRejectsAmbiguity()
    {
        await using var ownerIssues = new IssueStore(Path.Combine(_workDir, "single.db"), "owner");
        var owner = new SpecStore(ownerIssues);
        var spec = await owner.CreateAsync(new("owner", "Owner", "body"));
        var old = await _artOutputs.CreateAsync(new(spec.Id, ArtOutputKind.Mesh, "History", spec.Id + "/old.glb", "glb"));
        var history = new OwnedArtifactHistoryStore(ownerIssues.Db);
        await history.ImportAsync(new("writer", _issues.Db, _workDir), owner);
        var registry = new ProjectStore(_issues);
        await registry.UpsertAsync(new("owner", "Owner", "https://example.invalid/owner.git"));
        await using var projects = new Forge.Projects.ProjectContextFactory(registry, _workDir,
            dbResolver: (_, _) => throw new InvalidOperationException("Typed primary already exists"));
        var routing = new ProjectRoutingSpecStore(owner, _ => owner,
            () => throw new InvalidOperationException("No project initialization is needed"),
            () => projects.GetInitializedHistoryStores(owner));
        Assert.Equal(old.Id, (await routing.ReadHistoricalArtAcrossProjectsAsync(old.Id, default))!.Artifact.Id);
        await history.ImportAsync(new("different-writer", _issues.Db, _workDir), owner);
        await Assert.ThrowsAsync<ArtifactHistoryAmbiguousException>(() =>
            routing.ReadHistoricalArtAcrossProjectsAsync(old.Id, default));
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
