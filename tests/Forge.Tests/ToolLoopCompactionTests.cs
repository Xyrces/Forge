using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Tool-loop compaction wiring (operator-approved 2026-08-06, after
/// task-560 died mid-run at a 481KB transcript): the chat reducer
/// must sit INSIDE the function-invocation middleware so every
/// model→tool→model round-trip is budget-checked. If the pipeline
/// order regresses (reducer outermost), it only ever sees the first
/// request and the tool loop grows unbounded again — this test is
/// the tripwire.
/// </summary>
public class ToolLoopCompactionTests
{
    private sealed class ScriptedToolCallClient : IChatClient
    {
        public int Calls;
        public List<int> MessageCountsPerCall = new();
        public bool SawToolResult;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            var list = messages.ToList();
            MessageCountsPerCall.Add(list.Count);
            if (list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
            {
                SawToolResult = true;
            }
            if (Calls == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    new AIContent[]
                    {
                        new FunctionCallContent("call-1", "echo",
                            new Dictionary<string, object?> { ["text"] = "hello" }),
                    })));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CountingReducer : IChatReducer
    {
        public int Calls;
        public List<int> MessageCounts = new();

        public Task<IEnumerable<ChatMessage>> ReduceAsync(
            IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            MessageCounts.Add(messages.Count());
            return Task.FromResult(messages);
        }
    }

    [Fact]
    public async Task Reducer_SeesEveryToolLoopIteration()
    {
        var inner = new ScriptedToolCallClient();
        var reducer = new CountingReducer();
        var echo = AIFunctionFactory.Create((string text) => text, name: "echo");

        // Production order (MafAgentRunner.BuildToolLoopClient):
        // UseFunctionInvocation FIRST, then UseChatReducer.
        var client = new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .UseChatReducer(reducer, configure: null)
            .Build();

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "go") },
            new ChatOptions { Tools = new[] { echo } });

        Assert.Equal("done", response.Text);
        Assert.Equal(2, inner.Calls);
        Assert.True(inner.SawToolResult);
        // The tripwire: the reducer ran once per FI iteration (both
        // the initial request AND the post-tool-result request), and
        // its second invocation saw the grown conversation.
        Assert.Equal(2, reducer.Calls);
        Assert.True(reducer.MessageCounts[1] > reducer.MessageCounts[0],
            $"expected the reducer's second invocation to see the tool-result messages (counts: {string.Join(",", reducer.MessageCounts)})");
    }
}
