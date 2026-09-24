using System.Runtime.CompilerServices;
using Forge.Agents;
using Forge.Core;
using Forge.Tools.E2E;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class BenchmarkMeteringTests : IDisposable
{
    private readonly string _root = TempRoot.Instance.NewDirectory("benchmark-metering");
    private static readonly LlmConfig Config = new(
        new ProviderConfig("test", "https://invalid.example", "unused", null, "test-model"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CompletedCallCapsOutputAndPersistsUsageWithoutPrompt()
    {
        const string secretPrompt = "PROMPT-MUST-NOT-ENTER-LEDGER";
        var inner = new FakeChatClient();
        inner.Responses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 12,
                OutputTokenCount = 4,
                CachedInputTokenCount = 3,
            },
        });
        var ledger = Path.Combine(_root, "usage.json");
        var factory = Factory(inner, ledger, maxCalls: 1, maxInput: 1_000, maxOutput: 5);
        var client = factory.Create(Config, AgentType.CoreDev);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, secretPrompt)],
            new ChatOptions { MaxOutputTokens = 99 });

        Assert.Equal(5, inner.LastOptions?.MaxOutputTokens);
        Assert.Equal(1, factory.Snapshot.Calls);
        Assert.Equal(12, factory.Snapshot.InputTokens);
        Assert.Equal(4, factory.Snapshot.OutputTokens);
        Assert.Equal(3, factory.Snapshot.CachedInputTokens);
        Assert.True(factory.Snapshot.AccountingComplete);
        Assert.DoesNotContain(secretPrompt, await File.ReadAllTextAsync(ledger), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCallCountsAndLeavesUsageExplicitlyUnknown()
    {
        var inner = new FakeChatClient { Failure = new InvalidOperationException("provider failed") };
        var factory = Factory(inner, Path.Combine(_root, "failed.json"));
        var client = factory.Create(Config, AgentType.CoreDev);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "small")]));

        Assert.Equal(1, factory.Snapshot.Calls);
        Assert.Equal(1, factory.Snapshot.FailedCalls);
        Assert.Equal(1, factory.Snapshot.MissingUsageCalls);
        Assert.False(factory.Snapshot.AccountingComplete);
        Assert.Null(factory.Snapshot.EstimatedCostUsd);
    }

    [Fact]
    public async Task ReportedLimitBreachLatchesAndBlocksLaterProviderCalls()
    {
        var inner = new FakeChatClient();
        inner.Responses.Enqueue(Response(input: 101, output: 2));
        inner.Responses.Enqueue(Response(input: 1, output: 1));
        var factory = Factory(inner, Path.Combine(_root, "breach.json"), maxInput: 100);
        var client = factory.Create(Config, AgentType.CoreDev);

        await Assert.ThrowsAsync<BenchmarkLimitExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "small")]));
        await Assert.ThrowsAsync<BenchmarkLimitExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "small again")]));

        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, factory.Snapshot.FailedCalls);
    }

    [Fact]
    public async Task AbandonedStreamWithPartialUsageRemainsUnknown()
    {
        var inner = new FakeChatClient { StreamUsage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 2 } };
        var factory = Factory(inner, Path.Combine(_root, "stream.json"));
        var client = factory.Create(Config, AgentType.CoreDev);

        await using (var enumerator = client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "small")]).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        Assert.Equal(1, factory.Snapshot.FailedCalls);
        Assert.Equal(1, factory.Snapshot.MissingUsageCalls);
        Assert.False(factory.Snapshot.AccountingComplete);
        Assert.Equal(7, factory.Snapshot.InputTokens);
    }

    [Fact]
    public async Task OversizedTextIsRejectedBeforeProviderCall()
    {
        var inner = new FakeChatClient();
        var factory = Factory(inner, Path.Combine(_root, "oversized.json"), maxInput: 20);
        var client = factory.Create(Config, AgentType.CoreDev);

        await Assert.ThrowsAsync<BenchmarkLimitExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, new string('x', 100))]));

        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, factory.Snapshot.Calls);
    }

    [Fact]
    public async Task MaxCallsIsSharedAcrossClientsCreatedForDifferentRoles()
    {
        var inner = new FakeChatClient();
        inner.Responses.Enqueue(Response(input: 4, output: 1));
        inner.Responses.Enqueue(Response(input: 5, output: 2));
        var factory = Factory(inner, Path.Combine(_root, "shared-limit.json"), maxCalls: 2);
        var coreClient = factory.Create(Config, AgentType.CoreDev);
        var reviewerClient = factory.Create(Config, AgentType.Reviewer);

        await coreClient.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        await reviewerClient.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]);
        await Assert.ThrowsAsync<BenchmarkLimitExceededException>(() =>
            coreClient.GetResponseAsync([new ChatMessage(ChatRole.User, "third")]));

        Assert.Equal(2, inner.Calls);
        Assert.Equal(2, factory.Snapshot.Calls);
        Assert.Equal(9, factory.Snapshot.InputTokens);
        Assert.Equal(3, factory.Snapshot.OutputTokens);
    }

    [Fact]
    public void ExistingLedgerIsNeverOverwritten()
    {
        var ledger = Path.Combine(_root, "existing-ledger.json");
        _ = Factory(new FakeChatClient(), ledger);
        var original = File.ReadAllText(ledger);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Factory(new FakeChatClient(), ledger));

        Assert.Contains("Refusing to overwrite", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(ledger));
    }

    private BenchmarkMeteringFactory Factory(
        FakeChatClient client,
        string ledger,
        int maxCalls = 3,
        int maxInput = 1_000,
        int maxOutput = 100) => new(
            new FakeFactory(client),
            new BenchmarkMeteringOptions(
                "attempt", "scenario", "test", "test-model",
                maxCalls, maxInput, maxOutput,
                InputUsdPerMillionTokens: 1m,
                OutputUsdPerMillionTokens: 2m),
            ledger);

    private static ChatResponse Response(long input, long output) =>
        new(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

    private sealed class FakeFactory(FakeChatClient client) : IChatClientFactory
    {
        public IChatClient Create(
            LlmConfig config,
            AgentType role,
            string? projectId = null,
            RoleModel? modelOverride = null) => client;
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Queue<ChatResponse> Responses { get; } = new();
        public Exception? Failure { get; init; }
        public UsageDetails? StreamUsage { get; init; }
        public ChatOptions? LastOptions { get; private set; }
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            return Failure is not null
                ? Task.FromException<ChatResponse>(Failure)
                : Task.FromResult(Responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new UsageContent(StreamUsage ?? new UsageDetails())]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unused");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
