using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Forge.Tests;

public sealed class OpenAIResponsesTests
{
    private const string Response = """
        {"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"test-model","store":false,"output":[{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque"},{"type":"function_call","id":"fc_1","call_id":"call_1","name":"inspect","arguments":"{}","status":"completed"}],"usage":{"input_tokens":10,"output_tokens":4,"total_tokens":14,"input_tokens_details":{"cached_tokens":3},"output_tokens_details":{"reasoning_tokens":2}}}
        """;

    [Fact]
    public async Task NativeResponsesRoundTripsToolsImagesAndReasoning_WithStatelessPolicy()
    {
        var handler = new CaptureHandler(Response);
        using var http = new HttpClient(handler);
        using var client = OpenAICompatibleChatClientFactory.Build(new("test", "https://example.test/v1", "fake", "org-test", "test-model",
            Api: "responses", MaxOutputTokens: 1000, ProjectId: "proj-test", ReasoningEffort: "High"), "test-model", httpClient: http);
        var options = new ChatOptions { Temperature = 0, MaxOutputTokens = 9000,
            Tools = [AIFunctionFactory.Create(() => "ok", "inspect")] };
        var messages = new List<ChatMessage> { new(ChatRole.User, [new TextContent("Inspect"), new UriContent("https://example.test/image.png", "image/png")]) };
        var response = await client.GetResponseAsync(messages, options);
        Assert.Equal(10, response.Usage!.InputTokenCount);
        Assert.Null(response.ConversationId);
        Assert.Equal("opaque", response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Single().ProtectedData);
        Assert.Equal("call_1", response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single().CallId);
        messages.AddRange(response.Messages);
        messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call_1", "ok")]));
        await client.GetResponseAsync(messages, options);
        using var doc = JsonDocument.Parse(handler.Bodies[1]);
        var body = doc.RootElement;
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal("default", body.GetProperty("service_tier").GetString());
        Assert.Equal(1000, body.GetProperty("max_output_tokens").GetInt32());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Contains("reasoning.encrypted_content", body.GetProperty("include").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("input_image", handler.Bodies[1]);
        Assert.Contains("function_call_output", handler.Bodies[1]);
        Assert.Contains("opaque", handler.Bodies[1]);
        Assert.Equal("/v1/responses", handler.Path);
        Assert.Equal("org-test", handler.Org);
        Assert.Equal("proj-test", handler.Project);
        Assert.Equal(9000, options.MaxOutputTokens); // caller options never mutated
        Assert.Equal(0, options.Temperature);
    }

    [Fact]
    public async Task NativeStreamingMapsTerminalUsageAndEncryptedReasoning()
    {
        var events = "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"OK\"}\n\n"
            + "data: {\"type\":\"response.output_item.done\",\"sequence_number\":2,\"output_index\":1,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"opaque\"}}\n\n"
            + "data: {\"type\":\"response.completed\",\"sequence_number\":3,\"response\":" + Response + "}\n\n";
        var handler = new CaptureHandler(events, "text/event-stream");
        using var http = new HttpClient(handler);
        using var client = OpenAICompatibleChatClientFactory.Build(new("test", "https://example.test/v1", "fake", "org-test", "test-model",
            Api: "responses", ProjectId: "proj-test"), "test-model", httpClient: http);
        var contents = new List<AIContent>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "go")]))
            contents.AddRange(update.Contents);
        Assert.Equal("OK", contents.OfType<TextContent>().Single().Text);
        Assert.Equal("opaque", contents.OfType<TextReasoningContent>().Single().ProtectedData);
        Assert.Equal(10, contents.OfType<UsageContent>().Single().Details.InputTokenCount);
        Assert.True(JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void FactoryPolicyReceivesAttributionAndSessionDisposalKeepsCachedClientAlive()
    {
        LlmRequestContext? context = null;
        using var factory = new OpenAICompatibleChatClientFactory { MaxConcurrentRequests = 0,
            RequestPolicy = (c, inner) => { context = c; return inner; } };
        var config = new LlmConfig(new ProviderConfig("p", "https://example.test/v1", "fake", null, "m", Api: "responses", MaxOutputTokens: 500));
        using (var first = factory.Create(config, AgentType.Reviewer, "forge")) { }
        using var second = factory.Create(config, AgentType.Reviewer, "forge");
        Assert.Equal(new LlmRequestContext("p", "m", AgentType.Reviewer, "forge", 500), context);
        Assert.NotNull(second.GetService<ChatClientMetadata>());
    }

    private sealed class CaptureHandler(string response, string contentType = "application/json") : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? Path, Org, Project;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Path = request.RequestUri!.AbsolutePath;
            Org = request.Headers.GetValues("OpenAI-Organization").Single();
            Project = request.Headers.GetValues("OpenAI-Project").Single();
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, contentType) };
        }
    }
}
