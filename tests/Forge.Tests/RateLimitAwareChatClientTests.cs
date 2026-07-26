using Microsoft.Extensions.AI;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// RateLimitAwareChatClient: the single funnel for LLM pressure
/// policy — fail-fast during cooldown, per-provider concurrency
/// permit, centralized 429 recording (operator 2026-07-24: 'we must
/// be hammering it').
/// </summary>
public class RateLimitAwareChatClientTests
{
    private static readonly ChatMessage[] Msgs = { new(ChatRole.User, "go") };

    [Fact]
    public async Task CoolingModel_ThrowsBeforeAnyHttpCall()
    {
        var tracker = new ModelRateLimitTracker();
        tracker.RecordRateLimit("gw", "m", TimeSpan.FromMinutes(3));
        var inner = new CountingClient();
        var client = new RateLimitAwareChatClient(inner, "gw", "m", tracker, new SemaphoreSlim(2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Msgs));
        Assert.Equal(0, inner.Calls);   // no request left the process
        // Message matches the 429 pattern so existing handlers
        // (IsLlmRateLimited) treat it like a provider 429.
        Assert.Contains("429", ex.Message);
        Assert.Contains("Too Many Requests", ex.Message);
        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task Provider429_IsRecordedInSharedTracker()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient { Failure = new HttpRequestException("Error 429 Too Many Requests: rate limit") };
        var client = new RateLimitAwareChatClient(inner, "gw", "m", tracker, new SemaphoreSlim(2));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Msgs));
        Assert.True(tracker.IsCoolingDown("gw", "m"));
        Assert.False(tracker.IsCoolingDown("gw", "other-model"));
    }

    [Fact]
    public async Task Permit_SerializesConcurrentRoundTrips()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient { Gate = new TaskCompletionSource() };
        var client = new RateLimitAwareChatClient(inner, "gw", "m", tracker, new SemaphoreSlim(1));

        var first = client.GetResponseAsync(Msgs);
        await WaitForAsync(() => inner.Calls == 1);
        var second = client.GetResponseAsync(Msgs);
        await Task.Delay(300);
        Assert.Equal(1, inner.Calls);   // second waits on the permit

        inner.Gate!.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Non429Failure_IsNotRecorded()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient { Failure = new HttpRequestException("500 server error") };
        var client = new RateLimitAwareChatClient(inner, "gw", "m", tracker, new SemaphoreSlim(2));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Msgs));
        Assert.False(tracker.IsCoolingDown("gw", "m"));
    }

    private static async Task WaitForAsync(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(50);
        Assert.True(cond());
    }

    private sealed class CountingClient : IChatClient
    {
        public int Calls;
        public Exception? Failure;
        public TaskCompletionSource? Gate;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is not null) await Gate.Task;
            if (Failure is not null) throw Failure;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
