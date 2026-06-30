using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PortHorizon.Agents.Agents;

public sealed class ScriptedChatClient : IChatClient
{
    private readonly ConcurrentQueue<ChatResponse> _responses = new();
    private readonly ChatMessage? _fallbackMessage;
    private int _inputTokens;
    private int _outputTokens;

    public ScriptedChatClient(params ChatResponse[] responses)
        : this((ChatMessage?)null, responses) { }

    public ScriptedChatClient(ChatMessage? fallback, params ChatResponse[] responses)
    {
        _fallbackMessage = fallback;
        foreach (var r in responses) _responses.Enqueue(r);
    }

    public void Enqueue(ChatResponse response) => _responses.Enqueue(response);
    public int InputTokens => _inputTokens;
    public int OutputTokens => _outputTokens;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (_responses.TryDequeue(out var scripted))
        {
            CountTokens(messages, scripted);
            return scripted;
        }
        if (_fallbackMessage is not null)
        {
            return new ChatResponse(_fallbackMessage);
        }
        throw new InvalidOperationException(
            "ScriptedChatClient ran out of scripted responses. Enqueue more ChatResponse objects or pass a fallback message to the constructor.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var full = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var msg in full.Messages)
        {
            // One update per message, content carried as a single string for
            // stub simplicity. Real providers emit multiple updates per
            // message; the MAF ChatClientAgent handles both shapes.
            yield return new ChatResponseUpdate(msg.Role, msg.Text);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private void CountTokens(IEnumerable<ChatMessage> messages, ChatResponse response)
    {
        var inputText = string.Join(" ", messages.Select(m => m.Text));
        _inputTokens += Math.Max(1, inputText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 4 / 3);
        var outputText = string.Join(" ", response.Messages.Select(m => m.Text));
        _outputTokens += Math.Max(1, outputText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 4 / 3);
    }
}

public sealed class StubbedChatClientFactory : IChatClientFactory
{
    public IChatClient Create(LlmConfig config)
    {
        if (config.Provider != LlmProviders.Stub)
        {
            throw new InvalidOperationException(
                $"StubbedChatClientFactory only supports Provider={LlmProviders.Stub}. " +
                $"Got Provider={config.Provider}. Real providers land in P0.5+.");
        }
        return new ScriptedChatClient(
            new ChatMessage(ChatRole.Assistant,
                "[stub] ScriptedChatClient has no scripted responses; tests must Enqueue() at least one."));
    }
}
