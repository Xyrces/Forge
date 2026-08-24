using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.AgentTools;
using Forge.Core;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// In-session pre-push verification: an engineering run whose
/// verification fails gets the output fed back INTO the same session
/// (like a plan-gate revision) instead of dying to a dispatch-level
/// requeue (user direction 2026-07-30).
/// </summary>
public class MafAgentRunnerVerifyLoopTests : IDisposable
{
    private readonly string _worktree;

    public MafAgentRunnerVerifyLoopTests()
    {
        _worktree = TempRoot.Instance.NewDirectory("verify-loop");
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { }
    }

    /// <summary>
    /// Sequence: submit_plan → critic VERDICT → "work done" → (verify
    /// feedback) "fixed it". Count-based: the critic call always comes
    /// between the submit_plan turn and the next main-loop turn.
    /// </summary>
    private sealed class VerifyScriptedChatClient : IChatClient
    {
        public int CallCount;
        public string LastUserText = "";
        public readonly List<string> ToolResults = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUserText = string.Concat(messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            foreach (var m in messages.Where(m => m.Role == ChatRole.Tool))
            {
                var t = string.Concat(m.Contents.OfType<FunctionResultContent>().Select(c => c.Result?.ToString()));
                if (!string.IsNullOrEmpty(t) && !ToolResults.Contains(t)) ToolResults.Add(t);
            }
            return CallCount switch
            {
                1 => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    new AIContent[]
                    {
                        new FunctionCallContent("c1", "submit_plan", new Dictionary<string, object?>
                        {
                            ["plan"] = "## Goal\nAdd it.\n## Files\n- Core/NewThing.cs (new)\n## Approach\nWrite it.\n## Test\ndotnet test\n## Done\nGreen.",
                        }),
                    }))),
                2 => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "VERDICT: APPROVE\nsound plan"))),
                3 => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "work done"))),
                _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixed it"))),
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptingFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null) => client;
    }

    [Fact]
    public async Task VerificationFailure_FeedsBackIntoSameSession_ThenPasses()
    {
        var client = new VerifyScriptedChatClient();
        var runner = new MafAgentRunner(
            chatClientFactory: new ScriptingFactory(client),
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("vl-md"),
            verifyCommandsLookup: _ => new[] { "dotnet test" });

        var verifyCalls = 0;
        runner.VerifyRunner = (dir, commands, logger, ct) =>
        {
            verifyCalls++;
            return Task.FromResult(verifyCalls == 1
                ? new RunVerification.Result(false, new[] { "`dotnet test` exited 1: SomeTest [FAIL]" })
                : new RunVerification.Result(true, Array.Empty<string>()));
        };

        var result = await runner.RunAsync(
            AgentType.CoreDev,
            "add the thing",
            sessionId: null,
            context: new Dictionary<string, object>
            {
                ["worktreePath"] = _worktree,
                ["issueId"] = "task-test",
                ["projectId"] = "porthorizon",
            },
            ct: default);

        Assert.True(result.Text == "fixed it", $"text={result.Text} verifyCalls={verifyCalls} tools=[{string.Join(" | ", client.ToolResults)}]");
        Assert.Equal(2, verifyCalls);
        // The failure output went back into the session as feedback.
        Assert.Contains("Pre-push verification failed (round 1/3)", client.LastUserText);
        Assert.Contains("SomeTest [FAIL]", client.LastUserText);
    }

    [Fact]
    public async Task VerificationPass_NoFeedbackTurn()
    {
        var client = new VerifyScriptedChatClient();
        var runner = new MafAgentRunner(
            chatClientFactory: new ScriptingFactory(client),
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("vp-md"),
            verifyCommandsLookup: _ => new[] { "dotnet test" });

        var verifyCalls = 0;
        runner.VerifyRunner = (dir, commands, logger, ct) =>
        {
            verifyCalls++;
            return Task.FromResult(new RunVerification.Result(true, Array.Empty<string>()));
        };

        var result = await runner.RunAsync(
            AgentType.CoreDev,
            "add the thing",
            sessionId: null,
            context: new Dictionary<string, object>
            {
                ["worktreePath"] = _worktree,
                ["issueId"] = "task-test",
                ["projectId"] = "porthorizon",
            },
            ct: default);

        Assert.Equal("work done", result.Text);
        Assert.Equal(1, verifyCalls);
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task NoPlanApproval_VerificationSkipped()
    {
        // QA/reviewer runs (no plan gate) never verify — no mutations
        // were allowed in the first place.
        var client = new VerifyScriptedChatClient();
        var runner = new MafAgentRunner(
            chatClientFactory: new ScriptingFactory(client),
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("vs-md"),
            verifyCommandsLookup: _ => new[] { "dotnet test" });

        var verifyCalls = 0;
        runner.VerifyRunner = (dir, commands, logger, ct) =>
        {
            verifyCalls++;
            return Task.FromResult(new RunVerification.Result(true, Array.Empty<string>()));
        };

        await runner.RunAsync(
            AgentType.QA,
            "look at the thing",
            sessionId: null,
            context: new Dictionary<string, object> { ["worktreePath"] = _worktree },
            ct: default);

        Assert.Equal(0, verifyCalls);
    }
}
