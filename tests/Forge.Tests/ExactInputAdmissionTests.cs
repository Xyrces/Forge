using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Control;
using Forge.Orchestrator.Control;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class ExactInputAdmissionTests
{
    private const string Response = """
        {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-6.1-sol","output":[{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque-reasoning"},{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"OK","annotations":[]}]}],"usage":{"input_tokens":1000,"output_tokens":2,"total_tokens":1002,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}}
        """;

    private static AgentOptions Options() => new()
    {
        Llm = new() { Providers = [new() { Name = "openai", Api = "responses", BaseUrl = "https://api.openai.com/v1" }] },
        Budget = new() { PaidCallsEnabled = true, MaximumRequestUsd = 12m, Models = new() {
            ["gpt-6.1-sol"] = new() { InputUsdPerMillion = 10, OutputUsdPerMillion = 15, MaximumInputTokens = 1050000 } } }
    };

    private static IChatClient Native(HttpClient http) => OpenAICompatibleChatClientFactory.Build(
        new("openai", "https://api.openai.com/v1", "fake-key", "org-test", "gpt-6.1-sol",
            Api: "responses", MaxOutputTokens: 100, ProjectId: "project-test", ReasoningEffort: "High"),
        "gpt-6.1-sol", httpClient: http);

    private static IChatClient Budget(IChatClient inner, Ledger ledger, Func<CancellationToken, Task>? owner = null)
        => new BudgetRequestPolicy.BudgetChatClient(inner, ledger, Options(),
            new("openai", "gpt-6.1-sol", AgentType.CoreDev, "forge", 100), owner ?? (_ => Task.CompletedTask));

    [Theory]
    [InlineData(false, false, .0011)]
    [InlineData(true, false, .0011)]
    [InlineData(false, true, .0005)]
    [InlineData(true, true, .0005)]
    public async Task NativeOptionalWriteFieldsProduceTruthfullyClassifiedSettlement(bool stream, bool zeroWrites, decimal expected)
    {
        var ledger = new Ledger(); var handler = new Wire(ledger) { Stream = stream,
            ResponseBody = Response.Replace("\"model\":", "\"service_tier\":\"default\",\"model\":")
                .Replace("\"cached_tokens\":0", "\"cached_tokens\":800" + (zeroWrites ? ",\"cache_write_tokens\":0" : "")) };
        using var http = new HttpClient(handler); using var native = Native(http); using var client = Budget(native, ledger);
        if (stream) { await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "test")])) { } }
        else await client.GetResponseAsync([new(ChatRole.User, "test")]);
        Assert.Equal(expected, Assert.Single(ledger.Settled));
        Assert.EndsWith(zeroWrites ? ":usage-estimate" : ":cache-aware-upper-bound", Assert.Single(ledger.Bases));
    }

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "incomplete")]
    [InlineData(true, "incomplete")]
    public async Task NativeUsagePreservesUnknownCacheWriteFieldsAndSettlesBothPaths(bool stream, string status)
    {
        var ledger = new Ledger();
        var handler = new Wire(ledger) { Stream = stream, EventType = "response." + status, ResponseBody = Response
            .Replace("\"status\":\"completed\"", "\"status\":\"" + status + "\"")
            .Replace("\"model\":", "\"service_tier\":\"default\",\"model\":")
            .Replace("\"cached_tokens\":0", "\"cached_tokens\":800,\"cache_write_tokens\":150,\"cache_write_tokens_details\":{\"ttl_30m_tokens\":100,\"ttl_12h_tokens\":50}") };
        using var http = new HttpClient(handler); using var native = Native(http); using var client = Budget(native, ledger);
        if (stream) { await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "test")])) { } }
        else await client.GetResponseAsync([new(ChatRole.User, "test")]);
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
        Assert.Equal(.0007m, Assert.Single(ledger.Settled));
        var evidence = Assert.Single(ledger.Evidence);
        Assert.Contains("ttl_12h_tokens", evidence);
        Assert.DoesNotContain("opaque-reasoning", evidence);
        Assert.DoesNotContain("output_text", evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalNativeBodyIsCountedIncludingReplayAndSchemaBeforeSingleGeneration(bool stream)
    {
        var ledger = new Ledger(); var handler = new Wire(ledger);
        using var http = new HttpClient(handler);
        using var native = Native(http);
        // Obtain SDK-native opaque reasoning replay without guessing its representation.
        var first = await native.GetResponseAsync([new(ChatRole.User, "initial")]);
        handler.Generated.Clear();
        using var budget = Budget(native, ledger);
        var input = new List<ChatMessage> { new(ChatRole.User, "inspect") };
        input.AddRange(first.Messages);
        input.Add(new(ChatRole.User, "continue"));
        var options = new ChatOptions { Instructions = "system contract", MaxOutputTokens = 900,
            Tools = [AIFunctionFactory.Create((string path) => path, "inspect_file")],
            ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(new { type = "object", properties = new { answer = new { type = "string" } } })) };
        handler.Stream = stream;
        handler.AfterCount = () =>
        {
            input[0].Contents.Add(new TextContent("MUTATED_AFTER_COUNT"));
            options.Instructions = "MUTATED_AFTER_COUNT";
        };
        if (stream) { await foreach (var update in budget.GetStreamingResponseAsync(input, options)) { } }
        else await budget.GetResponseAsync(input, options);

        using var counted = JsonDocument.Parse(Assert.Single(handler.Counted));
        using var generated = JsonDocument.Parse(Assert.Single(handler.Generated));
        foreach (var field in new[] { "input", "instructions", "model", "tools", "text", "reasoning" })
            Assert.Equal(generated.RootElement.GetProperty(field).GetRawText(), counted.RootElement.GetProperty(field).GetRawText());
        Assert.Contains("opaque-reasoning", handler.Counted.Single());
        Assert.DoesNotContain("MUTATED_AFTER_COUNT", handler.Generated.Single());
        Assert.False(counted.RootElement.TryGetProperty("stream", out _));
        Assert.False(counted.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.Equal(100, generated.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(.0115m, Assert.Single(ledger.Reservations).ReservationUsd);
        Assert.Equal(.01003m, Assert.Single(ledger.Settled));
        Assert.Equal(new[] { "count", "reserve", "generate" }, ledger.Events);
        Assert.All(handler.Auth, auth => Assert.Equal("Bearer fake-key|org-test|project-test", auth));
    }

    [Theory]
    [InlineData("server-error")]
    [InlineData("malformed")]
    [InlineData("negative")]
    [InlineData("too-large")]
    [InlineData("output-does-not-fit")]
    [InlineData("wrong-object")]
    public async Task CounterFailuresNeverReserveOrGenerate(string failure)
    {
        var ledger = new Ledger(); var handler = new Wire(ledger) { Failure = failure };
        using var http = new HttpClient(handler); using var native = Native(http); using var client = Budget(native, ledger);
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new(ChatRole.User, "test")]));
        Assert.Single(handler.Counted); Assert.Empty(handler.Generated); Assert.Empty(ledger.Reservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipLossAfterCountStopsGeneration(bool afterReserve)
    {
        var ledger = new Ledger(); var handler = new Wire(ledger); var calls = 0;
        using var http = new HttpClient(handler); using var native = Native(http);
        using var client = Budget(native, ledger, _ => ++calls == (afterReserve ? 3 : 2)
            ? Task.FromException(new InvalidOperationException("owner lost")) : Task.CompletedTask);
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new(ChatRole.User, "test")]));
        Assert.Single(handler.Counted); Assert.Empty(handler.Generated);
        Assert.Equal(afterReserve ? 1 : 0, ledger.Reservations.Count);
        if (afterReserve) Assert.Null(Assert.Single(ledger.Settled));
    }

    [Fact]
    public async Task CancelledCounterNeverReservesOrGenerates()
    {
        var ledger = new Ledger(); var handler = new Wire(ledger) { Failure = "cancel" };
        using var http = new HttpClient(handler); using var native = Native(http); using var client = Budget(native, ledger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync([new(ChatRole.User, "test")]));
        Assert.Empty(handler.Generated); Assert.Empty(ledger.Reservations);
    }

    [Fact]
    public async Task StreamingCounterFailureNeverReservesOrGenerates()
    {
        var ledger = new Ledger(); var handler = new Wire(ledger) { Failure = "server-error", Stream = true };
        using var http = new HttpClient(handler); using var native = Native(http); using var client = Budget(native, ledger);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        { await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "test")])) { } });
        Assert.Single(handler.Counted); Assert.Empty(handler.Generated); Assert.Empty(ledger.Reservations);
    }

    [Fact]
    public async Task ConcurrentBufferedAndStreamingCallsKeepIndependentAdmissionScopes()
    {
        var ledger = new Ledger(); var handler = new Wire(ledger) { Interleave = true };
        using var http = new HttpClient(handler); using var native = Native(http);
        using var first = Budget(native, ledger);
        using var second = new BudgetRequestPolicy.BudgetChatClient(native, ledger, Options(),
            new("openai", "gpt-6.1-sol", AgentType.Reviewer, "forge", 50), _ => Task.CompletedTask);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffered = first.GetResponseAsync([new(ChatRole.User, "buffered")], cancellationToken: timeout.Token);
        async Task StreamAsync()
        { await foreach (var update in second.GetStreamingResponseAsync([new(ChatRole.User, "stream")], cancellationToken: timeout.Token)) { } }
        await Task.WhenAll(buffered, StreamAsync());
        Assert.Equal(2, handler.Counted.Count);
        Assert.Equal(2, handler.Generated.Count);
        Assert.Equal(new[] { .01075m, .0115m }, ledger.Reservations.Select(r => r.ReservationUsd).Order());
        Assert.Equal(2, ledger.Reservations.Select(r => r.RequestId).Distinct().Count());
        Assert.Equal(2, ledger.Settled.Count);
        Assert.Null(ResponsesInputAdmission.Current);
    }

    [Theory]
    [InlineData("input_image")]
    [InlineData("input_file")]
    public void RawToolOutputMediaIsRejectedBeforeCounting(string type)
    {
        using var scope = new ResponsesInputAdmission("gpt-6.1-sol", 100, 1050000, (_, _, _) => Task.CompletedTask);
        var body = JsonSerializer.SerializeToUtf8Bytes(new {
            model = "gpt-6.1-sol", service_tier = "default", store = false, max_output_tokens = 100,
            input = new[] { new { type = "function_call_output", call_id = "call_1", output = new[] { new { type } } } } });
        Assert.Throws<InvalidOperationException>(() => ResponsesInputCountingHandler.ProjectCountBody(body, scope, out _));
    }

    [Fact]
    public async Task GenerationFailureIsNotRetriedAndRetainsReservation()
    {
        var ledger = new Ledger(); var handler = new Wire(ledger) { Failure = "generation" };
        using var http = new HttpClient(handler); using var native = Native(http); using var client = Budget(native, ledger);
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([new(ChatRole.User, "test")]));
        Assert.Single(handler.Counted); Assert.Single(handler.Generated);
        Assert.Single(ledger.Reservations); Assert.Null(Assert.Single(ledger.Settled));
    }

    private sealed class Wire(Ledger ledger) : HttpMessageHandler
    {
        public string? Failure;
        public string ResponseBody = Response;
        public string EventType = "response.completed";
        public bool Stream;
        public bool Interleave;
        private readonly TaskCompletionSource _bothCounting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? AfterCount;
        public List<string> Counted = [], Generated = [], Auth = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Auth) Auth.Add(request.Headers.Authorization + "|" + request.Headers.GetValues("OpenAI-Organization").Single()
                + "|" + request.Headers.GetValues("OpenAI-Project").Single());
            if (request.RequestUri!.AbsolutePath == "/v1/responses/input_tokens")
            {
                lock (Counted)
                {
                    Counted.Add(body);
                    if (Counted.Count == 2) _bothCounting.TrySetResult();
                }
                lock (ledger.Events) ledger.Events.Add("count");
                if (Interleave) await _bothCounting.Task.WaitAsync(ct);
                else Assert.Empty(ledger.Reservations);
                AfterCount?.Invoke();
                if (Failure == "cancel") throw new OperationCanceledException();
                if (Failure == "server-error") return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                var result = Failure switch { "malformed" => "{}", "negative" => "{\"object\":\"response.input_tokens\",\"input_tokens\":-1}",
                    "too-large" => "{\"object\":\"response.input_tokens\",\"input_tokens\":1050001}",
                    "output-does-not-fit" => "{\"object\":\"response.input_tokens\",\"input_tokens\":1050000}",
                    "wrong-object" => "{\"object\":\"response\",\"input_tokens\":1000}",
                    _ => "{\"object\":\"response.input_tokens\",\"input_tokens\":1000}" };
                return new(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
            }
            lock (Generated) Generated.Add(body);
            if (ResponsesInputAdmission.Current is not null)
            {
                lock (ledger.Events) ledger.Events.Add("generate");
                if (!Interleave) Assert.Single(ledger.Reservations);
            }
            if (Failure == "generation") return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            using var generation = JsonDocument.Parse(body);
            var streaming = Stream || (generation.RootElement.TryGetProperty("stream", out var streamingFlag) && streamingFlag.GetBoolean());
            return new(HttpStatusCode.OK) { Content = new StringContent(streaming
                ? "data: {\"type\":\"" + EventType + "\",\"sequence_number\":1,\"response\":" + ResponseBody + "}\n\n" : ResponseBody,
                Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }

    private sealed class Ledger : IDurableBudgetStore
    {
        public List<BudgetReservationRequest> Reservations = [];
        public List<decimal?> Settled = [];
        public List<string> Events = [];
        public List<string> Evidence = [];
        public List<string> Bases = [];
        public Task<bool> CompleteWithUsageAsync(string datasetId, string requestId, decimal? costUsd, string? usageJson, string pricingBasis, CancellationToken ct = default)
        { lock (Bases) Bases.Add(pricingBasis); if (usageJson is not null) lock (Evidence) Evidence.Add(usageJson); return CompleteAsync(datasetId, requestId, costUsd, ct); }

        public Task<BudgetReservationResult> ReserveAsync(BudgetReservationRequest request, CancellationToken ct = default)
        { lock (Events) Events.Add("reserve"); lock (Reservations) Reservations.Add(request); return Task.FromResult(new BudgetReservationResult(true, true, null, null)); }
        public Task<bool> CompleteAsync(string datasetId, string requestId, decimal? actualCostUsd, CancellationToken ct = default)
        { lock (Settled) Settled.Add(actualCostUsd); return Task.FromResult(true); }
        public Task<BudgetSnapshot?> ReadAsync(string datasetId, CancellationToken ct = default) => Task.FromResult<BudgetSnapshot?>(null);
    }
}
