using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 3: a per-task explicit model on the run context (the triage
/// escalation path — RunAgentExecutor stamps modelOverrideProvider/
/// modelOverrideModel from the consumed marker) wins over every other
/// resolution tier for THAT run: the factory receives the override
/// and the run registry labels the run with the escalated model. A
/// broken override degrades to the normal resolution instead of
/// killing the run.
/// </summary>
public class MafAgentRunnerModelOverrideTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _schema;
    private readonly AgentRunStore _runs;

    public MafAgentRunnerModelOverrideTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("mrmo");
        Directory.CreateDirectory(_workDir);
        _schema = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _runs = new AgentRunStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        _schema.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static LlmConfig Config() => new(
        Providers: new[]
        {
            new ProviderConfig("stub", "", null, null, "stub-model"),
            new ProviderConfig("premium", "", null, null, "premium-model"),
        },
        DefaultProvider: "stub",
        Roles: new Dictionary<AgentType, RoleModel>());

    [Fact]
    public async Task Run_WithContextModelOverride_FactoryReceivesOverride_RunLabelIsEscalatedModel()
    {
        var factory = new RecordingFactory();
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: Config(),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: _workDir,
            runs: _runs);
        var context = new Dictionary<string, object>
        {
            ["issueId"] = "task-esc",
            ["modelOverrideProvider"] = "premium",
            ["modelOverrideModel"] = "premium-x1",
        };

        var result = await runner.RunAsync(AgentType.CoreDev, "do the thing", sessionId: null, context, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.NotNull(factory.LastModelOverride);
        Assert.Equal("premium", factory.LastModelOverride!.ProviderName);
        Assert.Equal("premium-x1", factory.LastModelOverride.Model);
        var run = (await _runs.ListRecentAsync(taskId: "task-esc")).Single();
        Assert.Equal("premium-x1", run.Model);
    }

    [Fact]
    public async Task Run_WithoutOverride_FactoryGetsNull_LabelIsNormalModel()
    {
        var factory = new RecordingFactory();
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: Config(),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: _workDir,
            runs: _runs);
        var context = new Dictionary<string, object> { ["issueId"] = "task-normal" };

        await runner.RunAsync(AgentType.CoreDev, "do the thing", sessionId: null, context, CancellationToken.None);

        Assert.Null(factory.LastModelOverride);
        var run = (await _runs.ListRecentAsync(taskId: "task-normal")).Single();
        Assert.Equal("stub-model", run.Model);
    }

    [Fact]
    public async Task Run_BrokenOverride_FallsBackToNormalResolution()
    {
        // The provider named by a stale marker was removed from config
        // since the marker was written: the run must not die — it
        // falls back to the normal resolution and the label shows the
        // normal model.
        var factory = new RecordingFactory { ThrowOnOverride = true };
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: Config(),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: _workDir,
            runs: _runs);
        var context = new Dictionary<string, object>
        {
            ["issueId"] = "task-stale",
            ["modelOverrideProvider"] = "removed-provider",
            ["modelOverrideModel"] = "gone",
        };

        var result = await runner.RunAsync(AgentType.CoreDev, "do the thing", sessionId: null, context, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        var run = (await _runs.ListRecentAsync(taskId: "task-stale")).Single();
        Assert.Equal("stub-model", run.Model);
    }

    private sealed class RecordingFactory : IChatClientFactory
    {
        public RoleModel? LastModelOverride { get; private set; }
        public bool ThrowOnOverride { get; init; }

        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null)
        {
            LastModelOverride = modelOverride;
            if (ThrowOnOverride && modelOverride is not null)
            {
                // Mirror the real factory: an unconfigured provider
                // throws InvalidOperationException.
                _ = config.ResolveExplicit(modelOverride);
            }
            return new ScriptedChatClient(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }
    }
}
