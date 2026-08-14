using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Forge.Agents;

/// <summary>
/// Minimal non-streaming Anthropic Messages API client as an M.E.AI
/// <see cref="IChatClient"/>. Some providers (Kimi-for-Coding) expose
/// an Anthropic-protocol chat endpoint ({base}/messages, x-api-key
/// auth) alongside an OpenAI-shaped /models listing — the OpenAI
/// client 401s on chat there ("PAID_MODEL_AUTH_REQUIRED").
///
/// Scope: text + tool use round-trips, non-streaming. Streaming is
/// surfaced as a single buffered chunk carrying the FULL contents —
/// text alone would drop tool_use blocks from the function-invoking
/// pipeline (the intake agent streams through it).
/// </summary>
public sealed class AnthropicMessagesChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _defaultMaxOutputTokens;
    private readonly int? _thinkingBudgetTokens;

    /// <summary>Anthropic-protocol auth schemes observed in the wild:
    /// "x-api-key" (Kimi-for-Coding — a Bearer header makes its
    /// gateway 404) and "bearer" (MiniMax's /anthropic endpoint
    /// documents Authorization: Bearer for subscription keys).</summary>
    public const string AuthSchemeXApiKey = "x-api-key";
    public const string AuthSchemeBearer = "bearer";

    public AnthropicMessagesChatClient(string baseUrl, string apiKey, string model, HttpClient? http = null,
        int defaultMaxOutputTokens = 8192, int? thinkingBudgetTokens = null, string authScheme = AuthSchemeXApiKey)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.BaseAddress ??= new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey)
            && !_http.DefaultRequestHeaders.Contains("x-api-key")
            && !_http.DefaultRequestHeaders.Contains("Authorization"))
        {
            if (string.Equals(authScheme, AuthSchemeBearer, StringComparison.OrdinalIgnoreCase))
            {
                _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
            else
            {
                _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            }
        }
        if (!_http.DefaultRequestHeaders.Contains("anthropic-version"))
        {
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
        _model = model;
        _defaultMaxOutputTokens = defaultMaxOutputTokens;
        _thinkingBudgetTokens = thinkingBudgetTokens;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(messages, options);
        using var resp = await _http.PostAsJsonAsync("messages", body, JsonOpts, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            string detail;
            try
            {
                var err = JsonNode.Parse(raw);
                detail = err?["error"]?["message"]?.GetValue<string>() ?? raw;
            }
            catch (JsonException) { detail = raw; }
            // Keep the status + "rate limit" phrasing so
            // RateLimitAwareChatClient / IsLlmAuthFailure classify it.
            if ((int)resp.StatusCode == 429)
            {
                // Typed 429: carry Retry-After, the overload-vs-quota
                // classification, the provider's application code
                // (MiniMax 2056/2062 = account-level Token Plan
                // throttle) and its request id (support correlation)
                // up to RateLimitAwareChatClient — Kimi documents
                // Retry-After on overload responses, and the flat
                // default cooldown is wrong in both directions.
                var kind = LlmRateLimitException.Classify(raw);
                var code = LlmRateLimitException.ExtractErrorCode(raw);
                var requestId = LlmRateLimitException.ExtractRequestId(raw);
                throw new LlmRateLimitException(
                    $"HTTP 429 rate limit ({kind}{(code is not null ? $", provider code {code}" : "")}): {detail} " +
                    $"[uri={resp.RequestMessage?.RequestUri} request_id={requestId ?? "n/a"} body={raw[..Math.Min(300, raw.Length)]}]",
                    ParseRetryAfter(resp), kind, code, requestId);
            }
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode}: {detail} [uri={resp.RequestMessage?.RequestUri} body={raw[..Math.Min(300, raw.Length)]}]");
        }
        return ParseResponse(JsonNode.Parse(raw)!.AsObject());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Buffered "streaming": one update carrying the FULL response
        // contents. Yielding only response.Text silently drops tool_use
        // blocks — FunctionInvokingChatClient then never sees the
        // FunctionCallContent and never invokes the tool (live
        // 2026-08-14: the kimi intake said "creating the epic" while
        // its create_epic call vanished, and a tool-use-only reply
        // surfaced as an EMPTY assistant message that crashed
        // IntakeStore.AppendMessageAsync with "content is required").
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            response.Messages.SelectMany(m => m.Contents).ToList())
        {
            FinishReason = response.FinishReason,
            ResponseId = response.ResponseId,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
        // No-op by design: the factory caches one client per
        // (provider, model) for the process lifetime, while some
        // callers (IntakeAgent) dispose their chat client after every
        // run. Disposing the shared HttpClient here would poison the
        // cache entry ("Cannot access a disposed object" on the next
        // run). The HttpClient dies with the process.
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        var h = resp.Headers.RetryAfter;
        if (h?.Delta is { } delta && delta > TimeSpan.Zero && delta < TimeSpan.FromHours(1))
            return delta;
        if (h?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromHours(1))
                return wait;
        }
        return null;
    }

    private JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var system = new StringBuilder();
        var outMessages = new JsonArray();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                foreach (var c in msg.Contents)
                {
                    if (c is TextContent t) system.AppendLine(t.Text);
                }
                continue;
            }

            if (msg.Role == ChatRole.Tool)
            {
                // Tool results ride as a USER message with tool_result blocks.
                var blocks = new JsonArray();
                foreach (var c in msg.Contents)
                {
                    if (c is FunctionResultContent fr)
                    {
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = fr.CallId,
                            ["content"] = fr.Result?.ToString() ?? string.Empty,
                        });
                    }
                }
                if (blocks.Count > 0)
                {
                    outMessages.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
                }
                continue;
            }

            var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
            var content = new JsonArray();
            foreach (var c in msg.Contents)
            {
                switch (c)
                {
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                        break;
                    case FunctionCallContent fc:
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = fc.CallId,
                            ["name"] = fc.Name,
                            ["input"] = fc.Arguments is null
                                ? new JsonObject()
                                : JsonNode.Parse(JsonSerializer.Serialize(fc.Arguments)),
                        });
                        break;
                }
            }
            if (content.Count > 0)
            {
                outMessages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = options?.MaxOutputTokens ?? _defaultMaxOutputTokens,
            ["messages"] = outMessages,
        };
        if (system.Length > 0) body["system"] = system.ToString().TrimEnd();
        // Extended thinking (operator-approved 2026-08-01: Reviewer +
        // CoreDev, 4k budget): the response then carries `thinking`
        // content blocks, which the runner's transcript persists and
        // the Runs page renders as "model reasoning". Anthropic
        // requires max_tokens > budget_tokens (8192 > 4000 here).
        if (_thinkingBudgetTokens is > 0)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = _thinkingBudgetTokens.Value,
            };
        }
        if (options?.Temperature is not null) body["temperature"] = options.Temperature.Value;
        if (options?.TopP is not null) body["top_p"] = options.TopP.Value;
        if (options?.StopSequences is { Count: > 0 } stops)
        {
            body["stop_sequences"] = new JsonArray(stops.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        }

        var tools = options?.Tools?.OfType<AIFunction>().ToList();
        if (tools is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var f in tools)
            {
                var schema = f.JsonSchema.ValueKind is JsonValueKind.Object
                    ? JsonNode.Parse(f.JsonSchema.GetRawText())
                    : new JsonObject { ["type"] = "object" };
                arr.Add(new JsonObject
                {
                    ["name"] = f.Name,
                    ["description"] = f.Description ?? string.Empty,
                    ["input_schema"] = schema,
                });
            }
            body["tools"] = arr;
        }
        return body;
    }

    private ChatResponse ParseResponse(JsonObject root)
    {
        var contents = new List<AIContent>();
        foreach (var block in root["content"]?.AsArray() ?? new JsonArray())
        {
            if (block is not JsonObject b) continue;
            switch (b["type"]?.GetValue<string>())
            {
                case "thinking":
                    // Extended-thinking block: the reasoning rides in
                    // the "thinking" field. Surfaced as
                    // TextReasoningContent so the transcript pipeline
                    // persists + renders it (BuildTranscriptJson
                    // already handles that type).
                    contents.Add(new TextReasoningContent(b["thinking"]?.GetValue<string>() ?? string.Empty));
                    break;
                case "text":
                    contents.Add(new TextContent(b["text"]?.GetValue<string>() ?? string.Empty));
                    break;
                case "tool_use":
                    var id = b["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                    var name = b["name"]?.GetValue<string>() ?? string.Empty;
                    var input = b["input"] is JsonObject i
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(i.ToJsonString())
                        : null;
                    contents.Add(new FunctionCallContent(id, name, input));
                    break;
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, contents);
        var response = new ChatResponse(message)
        {
            ResponseId = root["id"]?.GetValue<string>(),
            FinishReason = root["stop_reason"]?.GetValue<string>() switch
            {
                "end_turn" => ChatFinishReason.Stop,
                "stop_sequence" => ChatFinishReason.Stop,
                "max_tokens" => ChatFinishReason.Length,
                "tool_use" => ChatFinishReason.ToolCalls,
                _ => null,
            },
        };
        if (root["usage"] is JsonObject usage)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = usage["input_tokens"]?.GetValue<int>(),
                OutputTokenCount = usage["output_tokens"]?.GetValue<int>(),
            };
            // Prompt-cache accounting (MiniMax reports these; real
            // Anthropic does too) — the difference between "the
            // conversation is huge" and "we're PAYING for huge".
            var cacheRead = usage["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
            var cacheCreate = usage["cache_creation_input_tokens"]?.GetValue<long>() ?? 0;
            if (cacheRead > 0 || cacheCreate > 0)
            {
                response.Usage.AdditionalCounts ??= new AdditionalPropertiesDictionary<long>();
                if (cacheRead > 0) response.Usage.AdditionalCounts["cache_read_input_tokens"] = cacheRead;
                if (cacheCreate > 0) response.Usage.AdditionalCounts["cache_creation_input_tokens"] = cacheCreate;
            }
        }
        return response;
    }
}
