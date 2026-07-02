using Microsoft.Extensions.AI;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Tests.Integration.TestHelpers;

/// <summary>
/// Test-only chat client that returns a function call on the
/// first <see cref="GetResponseAsync"/> call and a plain text on
/// the second. Drives one AIFunction invocation per
/// <c>ChatClientAgent.RunAsync</c>.
/// </summary>
public sealed class ToolCallingChatClient : IChatClient
{
    private readonly FunctionCallContent[] _functionCalls;
    private readonly string _followUpText;
    private int _callIndex;
    public ToolCallingChatClient(FunctionCallContent[] functionCalls, string followUpText)
    {
        _functionCalls = functionCalls;
        _followUpText = followUpText;
    }
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_callIndex == 0 && _functionCalls.Length > 0)
        {
            _callIndex++;
            var call = _functionCalls[0];
            var msg = new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call });
            return Task.FromResult(new ChatResponse(msg));
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _followUpText)));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Test-only IChatClientFactory that wraps a pre-built
/// <see cref="ScriptedChatClient"/>. <c>Create</c> always returns
/// the same instance.
/// </summary>
public sealed class ScriptingChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _client;
    public ScriptingChatClientFactory(IChatClient client) { _client = client; }
    public IChatClient Create(LlmConfig config, AgentType role) => _client;
}

/// <summary>
/// Test-only chat client that returns a sequence of function calls
/// (one per <see cref="GetResponseAsync"/> invocation) and a final
/// plain-text response. Drives multi-step AIFunction invocations
/// per <c>ChatClientAgent.RunAsync</c>.
/// </summary>
public sealed class MultiToolCallingChatClient : IChatClient
{
    private readonly FunctionCallContent[] _functionCalls;
    private readonly string _followUpText;
    private int _callIndex;
    public MultiToolCallingChatClient(FunctionCallContent[] functionCalls, string followUpText)
    {
        _functionCalls = functionCalls;
        _followUpText = followUpText;
    }
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_callIndex < _functionCalls.Length)
        {
            var call = _functionCalls[_callIndex];
            _callIndex++;
            var msg = new ChatMessage(ChatRole.Assistant, new[] { (AIContent)call });
            return Task.FromResult(new ChatResponse(msg));
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _followUpText)));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}