using System.Runtime.CompilerServices;
using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class StreamingPolicyTests
{
    [Fact]
    public async Task StreamHoldsPermitRecordsUsageAndReleasesOnDispose()
    {
        var fake = new StreamClient();
        using var permit = new SemaphoreSlim(1);
        var tracker = new CostTracker();
        using var client = new OpenAICompatibleChatClientFactory.UsageTrackingChatClient(
            new RateLimitAwareChatClient(fake, "p", "m", new ModelRateLimitTracker(), permit), tracker, AgentType.CoreDev);
        await using (var enumerator = client.GetStreamingResponseAsync([new(ChatRole.User, "go")]).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(0, permit.CurrentCount);
            Assert.Equal(10, tracker.Snapshot().TotalInputTokens);
        }
        Assert.Equal(1, permit.CurrentCount);
        Assert.True(fake.DisposedStream);
    }

    [Fact]
    public async Task StreamFailureCoolsProviderWithoutReplayingPartialOutput()
    {
        var fake = new StreamClient { Fail = true };
        var tracker = new ModelRateLimitTracker();
        using var permit = new SemaphoreSlim(1);
        using var client = new RateLimitAwareChatClient(fake, "p", "m", tracker, permit, sharedQuota: true);
        await Assert.ThrowsAsync<LlmRateLimitException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "go")])) { }
        });
        Assert.True(tracker.IsCoolingDown("p", "other"));
        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, permit.CurrentCount);
    }

    private sealed class StreamClient : IChatClient
    {
        public bool Fail, DisposedStream;
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            try
            {
                await Task.Yield();
                yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 })] };
                if (Fail) throw new LlmRateLimitException("429 quota", null, RateLimitKind.Quota);
            }
            finally { DisposedStream = true; }
        }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
}
