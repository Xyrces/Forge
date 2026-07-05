using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Codebase;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests;

/// <summary>
/// DesignerAgent integration tests. Use a scripted chat client
/// (the same pattern as GroomerAgent / EngineeringDispatchWorkflow
/// tests) so the LLM is deterministic.
///
/// The designer has two stages: a deterministic hygiene check
/// and an LLM step. These tests focus on the pipeline:
///   - hygiene failure short-circuits the LLM
///   - LLM that calls db_set_spec_status(Designed) + db_save_design_artifact
///     transitions the spec and writes the artifact
///   - LLM that calls db_set_spec_status(Approved) skips artifacts
///   - LLM that calls db_set_spec_status(NeedsRevision) reports error
///   - LLM that doesn't call db_set_spec_status fails the run
/// </summary>
public class DesignerAgentTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly DesignArtifactStore _artifacts;
    private readonly DesignerRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly CodebaseGraphCacheStore _graphCache;
    private readonly DotnetCodebaseGraphBuilder _graphBuilder;
    private readonly InMemoryDashboardEventBus _events;

    public DesignerAgentTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-designer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _specs = new SpecStore(_issues);
        _artifacts = new DesignArtifactStore(Path.Combine(_workDir, "issues.db"));
        _runs = new DesignerRunStore(Path.Combine(_workDir, "issues.db"));
        _memory = new MemoryStore(Path.Combine(_workDir, "memory.db"));
        _graphCache = new CodebaseGraphCacheStore(_issues);
        _graphBuilder = new DotnetCodebaseGraphBuilder();
        _events = new InMemoryDashboardEventBus();
    }

    public void Dispose()
    {
        _issues.Dispose();
        _specs.Dispose();
        _memory.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email t@t", dir);
        Run("git", "config user.name T", dir);
        var coreDir = Path.Combine(dir, "PortHorizon.Core");
        Directory.CreateDirectory(coreDir);
        File.WriteAllText(Path.Combine(coreDir, "PortHorizon.Core.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(coreDir, "Program.cs"), "class Program { }");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add .", dir);
        Run("git", "commit -q -m init", dir);
    }

    private static void Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private DesignerAgent NewAgent(IChatClient client)
    {
        var factory = new SingleClientFactory(client);
        var config = new LlmConfig(new ProviderConfig("stub", "", null, null, "stub-model"));
        var roles = new RoleAgentRegistry();
        var hygiene = new DesignHygieneChecker(_specs, _graphCache, _graphBuilder, _workDir);
        // Real logger factory so the DesignerAgent's diagnostic
        // LogInformation calls land in xunit's output. NullLogger
        // swallows them.
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(_out)));
        return new DesignerAgent(
            _specs, _artifacts, _runs, _memory, hygiene, factory, config, roles, _events,
            loggerFactory.CreateLogger<DesignerAgent>());
    }

    private const string HealthyBody = """
        ## Summary
        Inventory HUD wireframe.

        ## Acceptance criteria
        - [ ] inventory HUD shows current items
        - [ ] weight, quantity, and value are visible

        ## Touches
        - PortHorizon.Core

        ## Dependencies
        - none
        """;

    private async Task<SpecRecord> CreateSpecAsync(SpecStatus status = SpecStatus.ReadyForDesign)
    {
        var spec = await _specs.CreateAsync(new NewSpec("P", "Inventory HUD", HealthyBody));
        if (status != SpecStatus.Draft)
        {
            await _specs.SetStatusAsync(spec.Id, status);
        }
        return (await _specs.GetAsync(spec.Id))!;
    }

    /// <summary>Chat-client factory that always returns the same client.</summary>
    private sealed class SingleClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public SingleClientFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    /// <summary>xunit-output capture for designer logs.</summary>
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

    /// <summary>
    /// Tool-call-on-first-turn, plain-text-on-second. Drives the LLM to
    /// call a single AIFunction then settle.
    /// </summary>
    private sealed class ScriptedToolCallClient : IChatClient
    {
        private readonly string _toolName;
        private readonly Dictionary<string, object?> _args;
        private readonly string _finalText;
        public int CallCount;

        public ScriptedToolCallClient(string toolName, Dictionary<string, object?> args, string finalText)
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

    [Fact]
    public async Task HygieneFailure_ShortCircuitsLlmAndMarksRunHygieneFailed()
    {
        // Missing acceptance criteria -> Error finding -> LLM not called.
        var body = "## Summary\nx\n## Touches\n- PortHorizon.Core\n";
        var spec = await _specs.CreateAsync(new NewSpec("P", "Bad", body));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);

        var scripted = new ScriptedToolCallClient(
            "db_set_spec_status", new Dictionary<string, object?> { ["specId"] = spec.Id, ["status"] = "Designed" },
            "ignored");
        var agent = NewAgent(scripted);
        var result = await agent.DesignSpecAsync(spec.Id, DesignerTriggerKind.Manual);

        Assert.False(result.Success);
        Assert.Equal(0, scripted.CallCount);  // LLM never called
        var run = (await _runs.ListAsync(spec.Id)).Single();
        Assert.Equal(DesignerRunStatus.HygieneFailed, run.Status);
    }

    [Fact]
    public async Task LlmsCallsDbSetSpecStatusDesigned_WritesArtifact_TransitionsSpec()
    {
        var spec = await CreateSpecAsync();

        // Round 1: save_artifact. Round 2: set_spec_status(Designed).
        var client = new TwoStepClient(
            ("db_save_design_artifact", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["kind"] = "wireframe",
                ["title"] = "Inventory HUD v1",
                ["body"] = "<html><body><h1>Inventory</h1></body></html>",
                ["bodyKind"] = "html",
            }, "design-test-001"),
            ("db_set_spec_status", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["status"] = "Designed",
            }, "ok"),
            finalText: "Designed. Wrote wireframe.");
        var agent = NewAgent(client);
        var result = await agent.DesignSpecAsync(spec.Id, DesignerTriggerKind.Manual);

        Assert.True(result.Success);
        Assert.Equal(SpecStatus.Designed, result.NewSpecStatus);
        Assert.Single(result.ArtifactIds);

        var fresh = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.Designed, fresh.Status);
        var arts = await _artifacts.ListBySpecAsync(spec.Id);
        Assert.Single(arts);
        Assert.Equal(DesignArtifactKind.Wireframe, arts[0].Kind);
    }

    [Fact]
    public async Task LlmsCallsDbSetSpecStatusApproved_NonVisual_SkipsArtifact()
    {
        var spec = await CreateSpecAsync();
        var client = new TwoStepClient(
            null,  // no artifact save
            ("db_set_spec_status", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["status"] = "Approved",
            }, "ok"),
            finalText: "Non-visual. Approved.");
        var agent = NewAgent(client);
        var result = await agent.DesignSpecAsync(spec.Id, DesignerTriggerKind.Manual);

        Assert.True(result.Success);
        Assert.Equal(SpecStatus.Approved, result.NewSpecStatus);
        Assert.Empty(result.ArtifactIds);
    }

    [Fact]
    public async Task LlmsCallsDbSetSpecStatusNeedsRevision_ReportsError()
    {
        var spec = await CreateSpecAsync();
        var client = new TwoStepClient(
            null,
            ("db_set_spec_status", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["status"] = "NeedsRevision",
            }, "ok"),
            finalText: "Found a broken dep.");
        var agent = NewAgent(client);
        var result = await agent.DesignSpecAsync(spec.Id, DesignerTriggerKind.Manual);

        Assert.True(result.Success);
        Assert.Equal(SpecStatus.NeedsRevision, result.NewSpecStatus);
    }

    [Fact]
    public async Task LlmDoesNotCallDbSetSpecStatus_FailsRunAndLeavesSpecInPlace()
    {
        var spec = await CreateSpecAsync();
        var client = new ScriptedToolCallClient(
            "db_save_design_artifact",
            new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["kind"] = "wireframe",
                ["title"] = "x",
                ["body"] = "x",
                ["bodyKind"] = "html",
            },
            "design-fake-001");
        var agent = NewAgent(client);
        var result = await agent.DesignSpecAsync(spec.Id, DesignerTriggerKind.Manual);

        Assert.False(result.Success);
        Assert.Contains("without committing a spec status", result.Error);
        var fresh = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.ReadyForDesign, fresh.Status);  // unchanged
        var run = (await _runs.ListAsync(spec.Id)).Single();
        Assert.Equal(DesignerRunStatus.LlmFailed, run.Status);
    }

    [Fact]
    public async Task HygieneReport_IsPersistedOnTheRun()
    {
        var spec = await CreateSpecAsync();
        var client = new TwoStepClient(
            null,
            ("db_set_spec_status", new Dictionary<string, object?>
            {
                ["specId"] = spec.Id,
                ["status"] = "Designed",
            }, "ok"),
            finalText: "ok");
        var agent = NewAgent(client);
        await agent.DesignSpecAsync(spec.Id, DesignerTriggerKind.Manual);

        var run = (await _runs.ListAsync(spec.Id)).Single();
        Assert.NotNull(run.HygieneReportJson);
        var report = JsonSerializer.Deserialize<HygieneReport>(run.HygieneReportJson!, DesignerHygieneJsonContext.Default.HygieneReport);
        Assert.NotNull(report);
        Assert.True(report!.Passed);
    }
}

/// <summary>
/// Two-step scripted client. The first tool call (if any) fires on
/// turn 1; the second fires on turn 2. After both, the LLM returns
/// plain text. Used to drive MAF's function-invocation middleware
/// for the Designer's save_artifact + set_spec_status flow.
/// </summary>
internal sealed class TwoStepClient : IChatClient
{
    private readonly (string Name, Dictionary<string, object?> Args, string Result)? _step1;
    private readonly (string Name, Dictionary<string, object?> Args, string Result) _step2;
    private readonly string _finalText;
    public int CallCount;

    public TwoStepClient(
        (string, Dictionary<string, object?>, string)? step1,
        (string, Dictionary<string, object?>, string) step2,
        string finalText)
    {
        _step1 = step1;
        _step2 = step2;
        _finalText = finalText;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (CallCount == 1 && _step1 is not null)
        {
            var s = _step1.Value;
            var call = new FunctionCallContent("c1", s.Name, s.Args);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
        }
        if (CallCount == 2 || (CallCount == 1 && _step1 is null))
        {
            var call = new FunctionCallContent("c2", _step2.Name, _step2.Args);
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