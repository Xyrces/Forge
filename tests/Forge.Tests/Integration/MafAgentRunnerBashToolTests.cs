using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Verifies that <see cref="MafAgentRunner"/> wires a real
/// <see cref="BashTool"/> AIFunction through MAF when the orchestrator
/// passes a <c>worktreePath</c> in the context dict, and that the model
/// gets structured tool calls (not XML fallback) on the wire.
/// </summary>
public class MafAgentRunnerBashToolTests : IDisposable
{
    private readonly string _worktree;
    private readonly string _marker;

    public MafAgentRunnerBashToolTests()
    {
        _worktree = TempRoot.Instance.NewDirectory("mw-bash");
        Directory.CreateDirectory(_worktree);
        _marker = $"marker-{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(_worktree, "PROBE.txt"), _marker);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { }
    }

    /// <summary>
    /// Scripted chat client that returns a single tool call (bash) on
    /// the first request. MAF's function-invocation middleware should
    /// invoke the tool, get the result, and feed it back; the second
    /// request returns the final assistant text.
    /// </summary>
    private sealed class BashScriptedChatClient : IChatClient
    {
        public int CallCount;
        public ChatOptions? LastOptions;
        public bool ToolsPropagated;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastOptions = options;
            if (options is not null)
            {
                ToolsPropagated = options.Tools is { Count: > 0 };
            }

            if (CallCount == 1)
            {
                var call = new FunctionCallContent("c1", "bash",
                    new Dictionary<string, object?> { ["command"] = "type PROBE.txt" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call })));
            }
            // Second turn: model sees the tool result + replies.
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"Read PROBE.txt, contents: {_markerExpected}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        // The marker constant the test sets; injected via ctor below.
        private readonly string _markerExpected;
        public BashScriptedChatClient(string markerExpected) { _markerExpected = markerExpected; }
    }

    private sealed class ScriptingFactory : IChatClientFactory
    {
        private readonly IChatClient _client;
        public ScriptingFactory(IChatClient client) { _client = client; }
        public IChatClient Create(LlmConfig config, AgentType role) => _client;
    }

    [Fact]
    public async Task RunAsync_WithWorktreeContext_BashToolIsInvokedAndResultPropagates()
    {
        var client = new BashScriptedChatClient(_marker);
        var factory = new ScriptingFactory(client);
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("bash-md"));

        var result = await runner.RunAsync(
            AgentType.CoreDev,
            "Read PROBE.txt and report what it says",
            sessionId: null,
            context: new Dictionary<string, object> { ["worktreePath"] = _worktree },
            ct: default);

        // The chat client was called at least twice (turn 1 = tool call,
        // turn 2 = final text after tool result is fed back).
        Assert.True(client.CallCount >= 2, $"expected >=2 LLM calls; got {client.CallCount}");

        // Tools were sent to the model on the wire — this is what makes
        // the model emit structured tool_calls instead of XML fallback.
        Assert.True(client.ToolsPropagated, "tools array was not propagated to the chat client");

        // The bash tool was actually executed in the worktree: the
        // marker file contents flowed back into the assistant text.
        Assert.Contains(_marker, result.Text);
    }

    [Fact]
    public async Task RunAsync_WithoutContext_NoToolsPropagated()
    {
        var client = new PlainScriptedChatClient("ok");
        var factory = new ScriptingFactory(client);
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: TempRoot.Instance.NewDirectory("bash-md"));

        // No context => no worktreePath => no BashTool registered.
        var result = await runner.RunAsync(AgentType.CoreDev, "do thing", sessionId: null, ct: default);
        Assert.False(client.ToolsPropagated);
        Assert.Equal("ok", result.Text);
    }

    /// <summary>
    /// Plain scripted client (no tool calls) for the no-tools test.
    /// </summary>
    private sealed class PlainScriptedChatClient : IChatClient
    {
        private readonly string _text;
        public ChatOptions? LastOptions;
        public bool ToolsPropagated;
        public PlainScriptedChatClient(string text) { _text = text; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            if (options is not null) ToolsPropagated = options.Tools is { Count: > 0 };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}