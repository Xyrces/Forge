using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public class AnthropicMessagesChatClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static (Forge.Agents.AnthropicMessagesChatClient Client, FakeHandler Handler) NewClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        return (new Forge.Agents.AnthropicMessagesChatClient("https://api.kimi.com/coding/v1", "test-key", "kimi-for-coding", http), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task RequestShape_SystemExtracted_HeadersSent()
    {        var (client, handler) = NewClient(_ => Json(HttpStatusCode.OK,
            """{"id":"m1","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}"""));

        var resp = await client.GetResponseAsync(new[]
        {
            new ChatMessage(ChatRole.System, "you are terse"),
            new ChatMessage(ChatRole.User, "hello"),
        });

        Assert.Equal("hi", resp.Text);
        Assert.Equal(3, resp.Usage?.InputTokenCount);
        Assert.Equal(1, resp.Usage?.OutputTokenCount);
        Assert.Equal(ChatFinishReason.Stop, resp.FinishReason);

        Assert.Equal("messages", handler.LastRequest!.RequestUri!.PathAndQuery.TrimStart('/').Split('/')[^1]);
        Assert.Equal("test-key", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("kimi-for-coding", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("you are terse", doc.RootElement.GetProperty("system").GetString());
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Single(msgs.EnumerateArray());
        Assert.Equal("user", msgs[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ToolUse_RoundTrip()
    {
        var (client, handler) = NewClient(_ => Json(HttpStatusCode.OK,
            """{"id":"m2","content":[{"type":"tool_use","id":"call_1","name":"bash","input":{"command":"ls"}}],"stop_reason":"tool_use","usage":{"input_tokens":5,"output_tokens":2}}"""));

        var tool = AIFunctionFactory.Create((string command) => "ok", name: "bash", description: "run a command");
        var resp = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "list files") },
            new ChatOptions { Tools = [tool] });

        var call = resp.Messages[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("bash", call.Name);
        Assert.Equal("ls", call.Arguments?["command"]?.ToString());
        Assert.Equal(ChatFinishReason.ToolCalls, resp.FinishReason);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var tools = doc.RootElement.GetProperty("tools");
        Assert.Single(tools.EnumerateArray());
        Assert.Equal("bash", tools[0].GetProperty("name").GetString());
        Assert.Equal("object", tools[0].GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ToolResult_MapsToUserToolResultBlock()
    {
        var (client, handler) = NewClient(_ => Json(HttpStatusCode.OK,
            """{"id":"m3","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn","usage":{"input_tokens":9,"output_tokens":1}}"""));

        await client.GetResponseAsync(new[]
        {
            new ChatMessage(ChatRole.User, "list files"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "bash", new Dictionary<string, object?> { ["command"] = "ls" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", "a.txt b.txt")]),
        });

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(3, msgs.Length);
        Assert.Equal("assistant", msgs[1].GetProperty("role").GetString());
        Assert.Equal("tool_use", msgs[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("user", msgs[2].GetProperty("role").GetString());
        var block = msgs[2].GetProperty("content")[0];
        Assert.Equal("tool_result", block.GetProperty("type").GetString());
        Assert.Equal("call_1", block.GetProperty("tool_use_id").GetString());
        Assert.Equal("a.txt b.txt", block.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AuthFailure_SurfacesStatusAndDetail()
    {
        var (client, _) = NewClient(_ => Json(HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"PAID_MODEL_AUTH_REQUIRED — sign in"}}"""));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Contains("401", ex.Message);
        Assert.Contains("PAID_MODEL_AUTH_REQUIRED", ex.Message);
    }

    [Fact]
    public async Task RateLimit_MessageContainsRateLimitPhrasing()
    {
        var (client, _) = NewClient(_ => Json(HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""));

        var ex = await Assert.ThrowsAsync<Forge.Agents.LlmRateLimitException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Contains("429", ex.Message);
        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task Overload429_ThrowsTypedException_WithRetryAfter()
    {
        var (client, _) = NewClient(_ =>
        {
            var resp = Json(HttpStatusCode.TooManyRequests,
                """{"type":"error","error":{"type":"rate_limit_error","message":"The engine is currently overloaded, please try again later"}}""");
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return resp;
        });

        var ex = await Assert.ThrowsAsync<Forge.Agents.LlmRateLimitException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        Assert.Equal(Forge.Agents.RateLimitKind.Overloaded, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Contains("429", ex.Message);
        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task Quota429_ClassifiedAsQuota_WithoutRetryAfter()
    {
        var (client, _) = NewClient(_ => Json(HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"Organization-level RPM limit reached"}}"""));

        var ex = await Assert.ThrowsAsync<Forge.Agents.LlmRateLimitException>(() =>
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

        Assert.Equal(Forge.Agents.RateLimitKind.Quota, ex.Kind);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task MaxTokens_UsesConfiguredDefault_AndHonorsPerCallOverride()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"id":"m4","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn"}"""));
        var client = new Forge.Agents.AnthropicMessagesChatClient(
            "https://api.kimi.com/coding/v1", "test-key", "kimi-for-coding",
            new HttpClient(handler), defaultMaxOutputTokens: 2048);

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });
        using (var doc = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") },
            new ChatOptions { MaxOutputTokens = 600 });
        using (var doc = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal(600, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ThinkingBudget_SendsThinkingBlock_ParsesReasoning()
    {
        // Operator-approved 2026-08-01 (Reviewer+CoreDev, 4k): the
        // request carries the anthropic thinking block and the
        // response's thinking content lands as TextReasoningContent —
        // the type the transcript pipeline persists and the Runs
        // page renders as "model reasoning".
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"id":"m5","content":[{"type":"thinking","thinking":"let me reason about this"},{"type":"text","text":"done"}],"stop_reason":"end_turn"}"""));
        var client = new Forge.Agents.AnthropicMessagesChatClient(
            "https://api.kimi.com/coding/v1", "test-key", "kimi-for-coding",
            new HttpClient(handler), thinkingBudgetTokens: 4000);

        var resp = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        using (var doc = JsonDocument.Parse(handler.LastBody!))
        {
            var thinking = doc.RootElement.GetProperty("thinking");
            Assert.Equal("enabled", thinking.GetProperty("type").GetString());
            Assert.Equal(4000, thinking.GetProperty("budget_tokens").GetInt32());
        }
        var reasoning = resp.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Single();
        Assert.Equal("let me reason about this", reasoning.Text);
        Assert.Equal("done", resp.Text);
    }

    [Fact]
    public async Task NoThinkingBudget_OmitsThinkingBlock()
    {
        var (client, handler) = NewClient(_ => Json(HttpStatusCode.OK,
            """{"id":"m6","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn"}"""));

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    }
}
