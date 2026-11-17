using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Control;
using Forge.Orchestrator.Control;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class BudgetRequestPolicyTests
{
    private static AgentOptions Options() => new() {
        Llm = new() { Providers = [new() { Name = "openai", Api = "responses", BaseUrl = "https://api.openai.com/v1" }] },
        Budget = new() { PaidCallsEnabled = true, MaximumRequestUsd = 12m, Models = new() { ["gpt-6.1-sol"] = new() {
            InputUsdPerMillion = 10, OutputUsdPerMillion = 15, MaximumInputTokens = 1050000 } } } };
    private static IChatClient Client(AgentOptions options, Ledger ledger, Transport transport,
        Func<CancellationToken, Task>? admission = null) => new BudgetRequestPolicy.BudgetChatClient(transport, ledger,
            options, new("openai", "gpt-6.1-sol", AgentType.CoreDev, "forge", 100), admission ?? (_ => Task.CompletedTask));
    private static ChatMessage[] Input() => [new(ChatRole.User, "hello")];

    [Theory]
    [InlineData("input-rate")]
    [InlineData("output-rate")]
    [InlineData("context")]
    [InlineData("regional")]
    [InlineData("proxy")]
    [InlineData("ceiling")]
    public async Task UnderqualifiedProfileNeverReservesOrCallsTransport(string failure)
    {
        var options = Options();
        var price = options.Budget.Models["gpt-6.1-sol"];
        if (failure == "input-rate") price.InputUsdPerMillion = 9.99m;
        if (failure == "output-rate") price.OutputUsdPerMillion = 14.99m;
        if (failure == "context") price.MaximumInputTokens = 1_049_999;
        if (failure == "regional") options.Llm.Providers[0].BaseUrl = "https://eu.api.openai.com/v1";
        if (failure == "proxy") options.Headroom.Enabled = true;
        if (failure == "ceiling") options.Budget.MaximumRequestUsd = .01m;
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(options, ledger, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Input()));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task UnknownModelCannotBorrowQualifiedPricing()
    {
        var options = Options();
        options.Budget.Models["unqualified-model"] = options.Budget.Models["gpt-6.1-sol"];
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = new BudgetRequestPolicy.BudgetChatClient(transport, ledger, options,
            new("openai", "unqualified-model", AgentType.CoreDev, "forge", 100), _ => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Input()));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task ModelOverrideIsRejectedBeforeReserving()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Input(), new() { ModelId = "expensive-model" }));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task CountedOpaqueReasoningReplayReservesExactInputAtQualifiedRates()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        var message = new ChatMessage(ChatRole.Assistant, "visible summary")
        {
            RawRepresentation = new { encrypted_content = "opaque-provider-state" },
        };
        await client.GetResponseAsync([message]);
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task InFlightSettlementUsesReservedPriceSnapshot()
    {
        var options = Options();
        var ledger = new Ledger(); var transport = new Transport(ledger)
        {
            OnBegin = () => options.Budget.Models["gpt-6.1-sol"].InputUsdPerMillion = 1m,
        };
        using var client = Client(options, ledger, transport);
        await client.GetResponseAsync(Input());
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
        Assert.Equal(.00013m, Assert.Single(ledger.Settlements));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("price")]
    [InlineData("provider")]
    [InlineData("legacy")]
    public async Task UnqualifiedRequestsNeverReserveOrReachTransport(string failure)
    {
        var options = Options();
        if (failure == "disabled") options.Budget.PaidCallsEnabled = false;
        if (failure == "price") options.Budget.Models.Clear();
        if (failure == "provider") options.Llm.Providers.Clear();
        if (failure == "legacy") options.Llm.Providers[0].Api = "openai";
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(options, ledger, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Input()));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothPathsReserveFirstClampOutputAndSettleKnownUsage(bool streaming)
    {
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        var caller = new ChatOptions { MaxOutputTokens = 900 };
        if (streaming) { await foreach (var update in client.GetStreamingResponseAsync(Input(), caller)) { } }
        else await client.GetResponseAsync(Input(), caller);
        Assert.Equal(900, caller.MaxOutputTokens);
        Assert.Equal(100, transport.OutputBound);
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
        Assert.Equal(.00013m, Assert.Single(ledger.Settlements));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsUnknownLiability(bool streaming)
    {
        var ledger = new Ledger(); var transport = new Transport(ledger) { Cancel = true };
        using var client = Client(Options(), ledger, transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            if (streaming) { await foreach (var update in client.GetStreamingResponseAsync(Input())) { } }
            else await client.GetResponseAsync(Input());
        });
        Assert.Single(ledger.Reservations);
        Assert.Null(Assert.Single(ledger.Settlements));
    }

    [Fact]
    public async Task RejectedReservationNeverCallsTransport()
    {
        var ledger = new Ledger { Admit = false }; var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Input()));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task AdmissionRecheckAfterReservationPreventsLostOwnerCall()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger); var calls = 0;
        using var client = Client(Options(), ledger, transport, _ => ++calls < 3 ? Task.CompletedTask : Task.FromException(new InvalidOperationException("owner changed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Input()));
        Assert.Equal(0, transport.Calls);
        Assert.Null(Assert.Single(ledger.Settlements));
    }

    [Fact]
    public async Task UnsupportedMediaNeverReserves()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        var input = new ChatMessage(ChatRole.User, [new UriContent(new Uri("https://example.test/image.png"), "image/png")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([input]));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task LargeInstructionsUseCountedTokensRatherThanSerializedBytes()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        await client.GetResponseAsync(Input(), new() { Instructions = new string('x', 1050000) });
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
    }

    [Fact]
    public async Task LargeStructuredSchemaUsesCountedTokensRatherThanSerializedBytes()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(Options(), ledger, transport);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", description = new string('x', 1050000) });
        await client.GetResponseAsync(Input(), new() { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema) });
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
    }

    [Fact]
    public async Task DisabledStreamingNeverReachesTransport()
    {
        var options = Options(); options.Budget.PaidCallsEnabled = false;
        var ledger = new Ledger(); var transport = new Transport(ledger);
        using var client = Client(options, ledger, transport);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            await foreach (var update in client.GetStreamingResponseAsync(Input())) { }
        });
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task PartialStreamDisposalRetainsUnknownLiability()
    {
        var ledger = new Ledger(); var transport = new Transport(ledger) { Partial = true };
        using var client = Client(Options(), ledger, transport);
        await using (var stream = client.GetStreamingResponseAsync(Input()).GetAsyncEnumerator())
            Assert.True(await stream.MoveNextAsync());
        Assert.Null(Assert.Single(ledger.Settlements));
    }

    private sealed class Ledger : IDurableBudgetStore
    {
        public bool Admit = true;
        public List<BudgetReservationRequest> Reservations = [];
        public List<decimal?> Settlements = [];
        public Task<BudgetReservationResult> ReserveAsync(BudgetReservationRequest request, CancellationToken ct = default)
        { Reservations.Add(request); return Task.FromResult(new BudgetReservationResult(Admit, true, null, "test")); }
        public Task<bool> CompleteAsync(string datasetId, string requestId, decimal? actualCostUsd, CancellationToken ct = default)
        { Settlements.Add(actualCostUsd); return Task.FromResult(true); }
        public Task<BudgetSnapshot?> ReadAsync(string datasetId, CancellationToken ct = default) => Task.FromResult<BudgetSnapshot?>(null);
    }

    private sealed class Transport(Ledger ledger) : IChatClient
    {
        public int Calls; public int? OutputBound; public bool Cancel, Partial;
        public Action? OnBegin;
        private async Task Begin(ChatOptions? options, CancellationToken ct)
        {
            var scope = ResponsesInputAdmission.Current!;
            scope.Claim();
            await scope.AdmitAsync(1000, options!.MaxOutputTokens!.Value, ct);
            Assert.Single(ledger.Reservations);
            Calls++; OutputBound = options?.MaxOutputTokens;
            OnBegin?.Invoke();
            if (Cancel) throw new OperationCanceledException("transport cancelled");
        }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { await Begin(options, cancellationToken); return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")) { Usage = Usage() }; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Begin(options, cancellationToken); await Task.Yield();
            if (Partial) yield return new() { Contents = [new TextContent("partial")] };
            yield return new() { Contents = [new UsageContent(Usage())] };
        }
        private static UsageDetails Usage() => new() { InputTokenCount = 10, OutputTokenCount = 2 };
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
