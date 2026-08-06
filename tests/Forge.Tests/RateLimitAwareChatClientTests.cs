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

    [Fact]
    public async Task Overload429_IsRetriedInPlace_ThenSucceeds()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient
        {
            Failures = new Exception[]
            {
                new LlmRateLimitException("HTTP 429 rate limit: The engine is currently overloaded", null, RateLimitKind.Overloaded),
                new LlmRateLimitException("HTTP 429 rate limit: The engine is currently overloaded", null, RateLimitKind.Overloaded),
            },
        };
        var client = new RateLimitAwareChatClient(inner, "kimi", "k3", tracker, new SemaphoreSlim(2),
            delay: (_, _) => Task.CompletedTask);

        var resp = await client.GetResponseAsync(Msgs);

        Assert.Equal("ok", resp.Text);
        Assert.Equal(3, inner.Calls);              // initial + 2 retries
        // The shed cooldown recorded during backoff is cleared on success.
        Assert.False(tracker.IsCoolingDown("kimi", "k3"));
    }

    [Fact]
    public async Task Overload429_RetriesExhausted_RecordsModelCooldown()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient
        {
            Failure = new LlmRateLimitException("HTTP 429 rate limit: The engine is currently overloaded",
                null, RateLimitKind.Overloaded),
        };
        var client = new RateLimitAwareChatClient(inner, "kimi", "k3", tracker, new SemaphoreSlim(2),
            overloadRetries: 2, delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<LlmRateLimitException>(() => client.GetResponseAsync(Msgs));

        Assert.Equal(3, inner.Calls);              // initial + 2 retries, then give up
        // No Retry-After hint → the tracker's default cooldown.
        var until = tracker.CoolingDownUntil("kimi", "k3");
        Assert.NotNull(until);
        Assert.True(until.Value > DateTime.UtcNow.AddMinutes(2));
        Assert.False(tracker.IsCoolingDown("kimi", "other-model"));  // overload cools per-model
    }

    [Fact]
    public async Task Overload429_WithLongRetryAfter_CoolsForHint_WithoutRetrying()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient
        {
            Failure = new LlmRateLimitException("HTTP 429 rate limit: The engine is currently overloaded",
                TimeSpan.FromMinutes(10), RateLimitKind.Overloaded),
        };
        var client = new RateLimitAwareChatClient(inner, "kimi", "k3", tracker, new SemaphoreSlim(2),
            delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<LlmRateLimitException>(() => client.GetResponseAsync(Msgs));

        Assert.Equal(1, inner.Calls);   // a hint that long is a real cooldown, not a blip
        var until = tracker.CoolingDownUntil("kimi", "k3");
        Assert.NotNull(until);
        Assert.True(until.Value > DateTime.UtcNow.AddMinutes(9));
    }

    [Fact]
    public async Task OverloadBackoff_ReleasesPermit_OtherModelProceeds()
    {
        var tracker = new ModelRateLimitTracker();
        var permit = new SemaphoreSlim(2);
        var delayGate = new TaskCompletionSource();
        var aInner = new CountingClient
        {
            Failure = new LlmRateLimitException("HTTP 429 rate limit: The engine is currently overloaded",
                null, RateLimitKind.Overloaded),
        };
        var a = new RateLimitAwareChatClient(aInner, "kimi", "k3", tracker, permit,
            overloadRetries: 1, delay: async (_, _) => await delayGate.Task);
        var b = new RateLimitAwareChatClient(new CountingClient(), "kimi", "kimi-for-coding", tracker, permit);

        var aCall = a.GetResponseAsync(Msgs);
        // A is now in backoff: shed cooldown recorded, permit released.
        await WaitForAsync(() => tracker.IsCoolingDown("kimi", "k3"));

        // B (same provider permit pool, different model) is not
        // starved behind A's sleep.
        var bResp = await b.GetResponseAsync(Msgs);
        Assert.Equal("ok", bResp.Text);

        delayGate.SetResult();
        await Assert.ThrowsAsync<LlmRateLimitException>(() => aCall);
    }

    [Fact]
    public async Task Quota429_IsNotRetried()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient
        {
            Failure = new LlmRateLimitException("HTTP 429 rate limit: Organization-level RPM limit reached",
                null, RateLimitKind.Quota),
        };
        var client = new RateLimitAwareChatClient(inner, "kimi", "k3", tracker, new SemaphoreSlim(2),
            delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<LlmRateLimitException>(() => client.GetResponseAsync(Msgs));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Quota429_WithSharedQuota_CoolsTheWholeProvider()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient
        {
            Failure = new LlmRateLimitException("HTTP 429 rate limit: Organization-level TPM limit reached",
                null, RateLimitKind.Quota),
        };
        var client = new RateLimitAwareChatClient(inner, "kimi", "k3", tracker, new SemaphoreSlim(2),
            sharedQuota: true, delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<LlmRateLimitException>(() => client.GetResponseAsync(Msgs));

        Assert.True(tracker.IsCoolingDown("kimi", "k3"));
        Assert.True(tracker.IsCoolingDown("kimi", "kimi-for-coding"));  // provider-wide
        Assert.False(tracker.IsCoolingDown("gw", "k3"));                // other provider untouched
    }

    [Fact]
    public async Task Quota429_WithoutSharedQuota_CoolsOnlyThatModel()
    {
        var tracker = new ModelRateLimitTracker();
        var inner = new CountingClient
        {
            Failure = new LlmRateLimitException("HTTP 429 rate limit: Organization-level RPM limit reached",
                null, RateLimitKind.Quota),
        };
        var client = new RateLimitAwareChatClient(inner, "gw", "m", tracker, new SemaphoreSlim(2),
            delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<LlmRateLimitException>(() => client.GetResponseAsync(Msgs));

        Assert.True(tracker.IsCoolingDown("gw", "m"));
        Assert.False(tracker.IsCoolingDown("gw", "other-model"));
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
        public IReadOnlyList<Exception>? Failures;
        public TaskCompletionSource? Gate;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref Calls);
            if (Gate is not null) await Gate.Task;
            if (Failures is not null && call <= Failures.Count) throw Failures[call - 1];
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
