using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Codebase;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Tests.Integration.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests.Integration;

/// <summary>
/// DesignerScheduler tests: the scheduler picks up ReadyForDesign
/// specs and runs the Designer on each. Each run is recorded in
/// designer_run. Re-runs specs whose last design failed. Skips
/// specs that were designed recently (within the configured
/// Interval) unless the last run failed.
///
/// <para>
/// The test uses a scripted chat client. The actual LLM behavior is
/// covered by DesignerAgentTests; this file covers the scheduler's
/// candidate-selection + retry logic.
/// </para>
/// </summary>
public class DesignerSchedulerTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly MemoryStore _memory;
    private readonly DesignArtifactStore _artifacts;
    private readonly DesignerRunStore _designerRuns;
    private readonly InMemoryDashboardEventBus _events;

    public DesignerSchedulerTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-dscheduler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _ = new IssueStore(_dbPath);  // v9 schema
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
        _memory = new MemoryStore(_dbPath);
        _artifacts = new DesignArtifactStore(_dbPath);
        _designerRuns = new DesignerRunStore(_dbPath);
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
        var coreDir = Path.Combine(dir, "PortHorizon.Client");
        Directory.CreateDirectory(coreDir);
        File.WriteAllText(Path.Combine(coreDir, "PortHorizon.Client.csproj"),
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

    /// <summary>
    /// Designer script: turn 1 calls db_save_design_artifact,
    /// turn 2 calls db_set_spec_status("Designed"). The follow-up
    /// text is the assistant's reply.
    /// </summary>
    private sealed class DesignerSuccessScript : IChatClient
    {
        private readonly string _specId;
        public int CallCount;
        public DesignerSuccessScript(string specId) { _specId = specId; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                var call = new FunctionCallContent("c1", "db_save_design_artifact",
                    new Dictionary<string, object?>
                    {
                        ["specId"] = _specId,
                        ["kind"] = "wireframe",
                        ["title"] = "wireframe",
                        ["body"] = "<html></html>",
                        ["bodyKind"] = "html",
                    });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
            }
            if (CallCount == 2)
            {
                var call = new FunctionCallContent("c2", "db_set_spec_status",
                    new Dictionary<string, object?>
                    {
                        ["specId"] = _specId,
                        ["status"] = "Designed",
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

    /// <summary>Designer script: every turn returns a text "no".</summary>
    private sealed class DesignerFailureScript : IChatClient
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

    private sealed class InlineFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public InlineFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    private async Task<SpecRecord> CreateReadySpecAsync(string title)
    {
        var spec = await _specs.CreateAsync(new NewSpec("PortHorizon", title, "## Summary\nx\n\n## Acceptance criteria\n- [ ] a\n\n## Touches\n- PortHorizon.Client\n\n## Dependencies\n- none\n"));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
        return (await _specs.GetAsync(spec.Id))!;
    }

    private DesignerAgentFactory NewDesignerFactory(IChatClient client) => new(
        _specs, _artifacts, _designerRuns, _memory,
        new DesignHygieneChecker(_specs, new CodebaseGraphCacheStore(_issues), new DotnetCodebaseGraphBuilder(), _workDir),
        new InlineFactory(client),
        new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")),
        new RoleAgentRegistry(),
        _events,
        NullLoggerFactory.Instance);

    [Fact]
    public async Task Tick_DesignStepDisabled_SkipsEverything()
    {
        // Pass 4 structural edit: design disabled in the workflow
        // definition = the pipeline runs the intake -> groom fast
        // path only; the scheduler must not run the designer at all.
        var spec = await CreateReadySpecAsync("No design pass");
        var disabled = Forge.Core.Workflow.WorkflowDefaults.Definition with
        {
            Steps = Forge.Core.Workflow.WorkflowDefaults.Definition.Steps
                .Select(s => s.Id == "design" ? s with { Enabled = false } : s).ToList(),
        };
        await _memory.RememberAsync(Forge.Core.Workflow.WorkflowResolver.LiveKey,
            Forge.Core.Workflow.WorkflowResolver.Serialize(disabled));
        var factory = NewDesignerFactory(new DesignerSuccessScript(spec.Id));
        var scheduler = new DesignerScheduler(
            _specs, factory, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5),
            workflow: new Forge.Core.Workflow.WorkflowResolver(_memory));

        await scheduler.TickAsync(default);

        var after = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.ReadyForDesign, after.Status);   // untouched
        Assert.Empty(await _designerRuns.ListAsync(specId: spec.Id, limit: 1, default));
    }

    [Fact]
    public async Task Tick_FindsReadyForDesignSpecs_AndRunsThem()
    {
        var spec = await CreateReadySpecAsync("Inventory HUD");
        var factory = NewDesignerFactory(new DesignerSuccessScript(spec.Id));
        var scheduler = new DesignerScheduler(
            _specs, factory, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));

        await scheduler.TickAsync(default);

        // After the tick, the spec should be Designed (the script's
        // turn 2 sets status=Designed), and a design_artifact row
        // should exist, and a designer_run row should be Succeeded.
        var after = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.Designed, after.Status);
        var arts = await _artifacts.ListBySpecAsync(spec.Id, status: null, default);
        Assert.Single(arts);
        Assert.Equal(DesignArtifactKind.Wireframe, arts[0].Kind);
        var runs = await _designerRuns.ListAsync(specId: spec.Id, limit: 1, default);
        Assert.Single(runs);
        Assert.Equal(DesignerRunStatus.Succeeded, runs[0].Status);
        Assert.Equal(SpecStatus.Designed, runs[0].NewSpecStatus);
    }

    [Fact]
    public async Task Tick_SkipsRecentlySucceededSpec()
    {
        var spec = await CreateReadySpecAsync("Test A");
        // First design run succeeds.
        var factory = NewDesignerFactory(new DesignerSuccessScript(spec.Id));
        var scheduler = new DesignerScheduler(_specs, factory, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler.TickAsync(default);

        // A second tick within the Interval should NOT re-run.
        var fresh = new DesignerSuccessScript(spec.Id);  // counts new calls
        var factory2 = NewDesignerFactory(fresh);
        var scheduler2 = new DesignerScheduler(_specs, factory2, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler2.TickAsync(default);
        Assert.Equal(0, fresh.CallCount);
    }

    [Fact]
    public async Task Tick_ReRunsSpecWhoseLastDesignFailed()
    {
        var spec = await CreateReadySpecAsync("Test B");
        // First run: failure (Designer agent didn't call db_set_spec_status
        // because the script returns plain text and the agent bails).
        var failureFactory = NewDesignerFactory(new DesignerFailureScript());
        var scheduler1 = new DesignerScheduler(_specs, failureFactory, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler1.TickAsync(default);

        // The first run should be recorded as LlmFailed (no status
        // transition because the agent didn't call db_set_spec_status).
        var firstRuns = await _designerRuns.ListAsync(specId: spec.Id, limit: 1, default);
        Assert.Single(firstRuns);
        Assert.Equal(DesignerRunStatus.LlmFailed, firstRuns[0].Status);

        // The scheduler should re-run on the next tick because the
        // last run failed.
        var successFactory = NewDesignerFactory(new DesignerSuccessScript(spec.Id));
        var scheduler2 = new DesignerScheduler(_specs, successFactory, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler2.TickAsync(default);

        var after = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.Designed, after.Status);
        var allRuns = await _designerRuns.ListAsync(specId: spec.Id, limit: 10, default);
        Assert.Equal(2, allRuns.Count);
        // Most recent run is Succeeded.
        Assert.Equal(DesignerRunStatus.Succeeded, allRuns[0].Status);
        Assert.Equal(DesignerRunStatus.LlmFailed, allRuns[1].Status);
    }

    [Fact]
    public async Task Tick_OnlyGroomsReadyForDesignSpecs_NotOtherStatuses()
    {
        // Specs in Draft, NeedsRevision, Designed, Approved should NOT
        // be picked up by the scheduler. Only ReadyForDesign.
        var ready = await CreateReadySpecAsync("Ready");
        var draft = await CreateReadySpecAsync("Draft");
        await _specs.SetStatusAsync(draft.Id, SpecStatus.Draft);
        var needsRev = await CreateReadySpecAsync("NeedsRev");
        await _specs.SetStatusAsync(needsRev.Id, SpecStatus.NeedsRevision);
        var designed = await CreateReadySpecAsync("Designed");
        await _specs.SetStatusAsync(designed.Id, SpecStatus.Designed);
        var approved = await CreateReadySpecAsync("Approved");
        await _specs.SetStatusAsync(approved.Id, SpecStatus.Approved);

        var factory = NewDesignerFactory(new DesignerSuccessScript(ready.Id));
        var scheduler = new DesignerScheduler(_specs, factory, _designerRuns, _events,
            NullLogger<DesignerScheduler>.Instance,
            interval: TimeSpan.FromMinutes(5));
        await scheduler.TickAsync(default);

        // Only `ready` should have run.
        var allRuns = await _designerRuns.ListAsync(limit: 100);
        Assert.Single(allRuns);
        Assert.Equal(ready.Id, allRuns[0].SpecId);
    }
}