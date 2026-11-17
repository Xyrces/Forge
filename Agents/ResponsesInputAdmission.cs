using System.Text.Json;

namespace Forge.Agents;

/// <summary>One-generation scope crossing SDK serialization into the HTTP boundary.</summary>
internal sealed class ResponsesInputAdmission : IDisposable
{
    private static readonly AsyncLocal<ResponsesInputAdmission?> Slot = new();
    private readonly ResponsesInputAdmission? _previous;
    private readonly Func<long, int, CancellationToken, Task> _admit;
    private int _claimed;
    internal static ResponsesInputAdmission? Current => Slot.Value;
    internal bool Admitted { get; private set; }
    internal string Model { get; }
    internal int MaxOutputTokens { get; }
    internal int ContextTokens { get; }

    internal ResponsesInputAdmission(string model, int maxOutputTokens, int contextTokens,
        Func<long, int, CancellationToken, Task> admit)
    {
        Model = model; MaxOutputTokens = maxOutputTokens; ContextTokens = contextTokens;
        _admit = admit; _previous = Slot.Value; Slot.Value = this;
    }

    internal void Claim()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0)
            throw new InvalidOperationException("A budget scope cannot send a second generation request.");
    }

    internal async Task AdmitAsync(long inputTokens, int outputTokens, CancellationToken ct)
    {
        if (inputTokens < 0 || inputTokens > ContextTokens || outputTokens <= 0 || outputTokens > MaxOutputTokens
            || inputTokens + outputTokens > ContextTokens)
            throw new InvalidOperationException("Counted request exceeds qualified model context/output limits.");
        await _admit(inputTokens, outputTokens, ct);
        Admitted = true;
    }

    public void Dispose() => Slot.Value = _previous;
}

/// <summary>Counts immutable final SDK JSON before allowing its generation request through.</summary>
internal sealed class ResponsesInputCountingHandler(HttpClient transport, bool ownsTransport) : HttpMessageHandler
{
    // Official input_tokens count schema, checked 2026-11-17. Input items are
    // copied whole, including opaque encrypted reasoning; never re-tokenized locally.
    // https://developers.openai.com/api/reference/resources/responses/subresources/input_tokens/methods/count
    private static readonly HashSet<string> CountFields = ["model", "input", "instructions", "tools", "tool_choice",
        "parallel_tool_calls", "reasoning", "text", "truncation", "personality"];
    private static readonly HashSet<string> GenerationFields = ["max_output_tokens", "stream", "store", "background",
        "include", "metadata", "safety_identifier", "user", "service_tier", "temperature", "top_p", "top_logprobs",
        "max_tool_calls", "stream_options", "prompt_cache_key", "prompt_cache_options", "prompt_cache_retention"];

    internal Func<byte[], CancellationToken, Task<byte[]>>? CountAsync { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var scope = ResponsesInputAdmission.Current;
        if (scope is null || request.RequestUri?.AbsolutePath == "/v1/responses/input_tokens")
            return await ForwardAsync(request, null, ct);
        scope.Claim();
        if (request.Method != HttpMethod.Post || request.RequestUri is null || request.RequestUri.AbsolutePath != "/v1/responses" || request.RequestUri.Scheme != "https"
            || request.RequestUri.Host != "api.openai.com" || !request.RequestUri.IsDefaultPort
            || request.RequestUri.Query.Length != 0 || request.Content is null)
            throw new InvalidOperationException("Unqualified Responses generation endpoint.");
        var body = await request.Content.ReadAsByteArrayAsync(ct);
        var countBody = ProjectCountBody(body, scope, out var outputTokens);
        var counter = CountAsync ?? throw new InvalidOperationException("Responses token counter is unavailable.");
        var counted = await counter(countBody, ct);
        using var result = JsonDocument.Parse(counted);
        if (!result.RootElement.TryGetProperty("object", out var kind) || kind.GetString() != "response.input_tokens"
            || !result.RootElement.TryGetProperty("input_tokens", out var count) || !count.TryGetInt64(out var inputTokens))
            throw new InvalidOperationException("Invalid Responses input token count.");
        await scope.AdmitAsync(inputTokens, outputTokens, ct);
        // Freeze what was counted. No subsequent SDK/model serializer can change it.
        ct.ThrowIfCancellationRequested();
        return await ForwardAsync(request, body, ct);
    }

    private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage original, byte[]? frozenBody, CancellationToken ct)
    {
        // HttpClient prohibits sending the same request object twice. Copy it,
        // retaining the exact serialized content bytes and authentication headers.
        using var forwarded = new HttpRequestMessage(original.Method, original.RequestUri)
        { Version = original.Version, VersionPolicy = original.VersionPolicy };
        foreach (var header in original.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in original.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (original.Content is not null)
        {
            forwarded.Content = new ByteArrayContent(frozenBody ?? await original.Content.ReadAsByteArrayAsync(ct));
            foreach (var header in original.Content.Headers)
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return await transport.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    internal static byte[] ProjectCountBody(byte[] body, ResponsesInputAdmission scope, out int outputTokens)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.GetProperty("model").GetString() != scope.Model
            || root.GetProperty("service_tier").GetString() != "default"
            || root.GetProperty("store").GetBoolean()
            || (root.TryGetProperty("background", out var background) && background.GetBoolean()))
            throw new InvalidOperationException("Final Responses request violates qualified policy.");
        outputTokens = root.GetProperty("max_output_tokens").GetInt32();
        if (root.TryGetProperty("tools", out var tools)
            && tools.EnumerateArray().Any(t => t.GetProperty("type").GetString() != "function"))
            throw new InvalidOperationException("Hosted tools are outside the qualified spending policy.");
        if (root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
            foreach (var item in input.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var itemType) ? itemType.GetString() : "message";
                if (type is not ("message" or "reasoning" or "function_call" or "function_call_output"))
                    throw new InvalidOperationException("Unqualified input item in counted request.");
                if (type == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                    && content.EnumerateArray().Any(c => c.GetProperty("type").GetString() is not ("input_text" or "output_text" or "refusal")))
                    throw new InvalidOperationException("Media input is outside the qualified spending policy.");
                if (type == "function_call_output" && item.TryGetProperty("output", out var toolOutput)
                    && toolOutput.ValueKind == JsonValueKind.Array
                    && toolOutput.EnumerateArray().Any(c => c.GetProperty("type").GetString() != "input_text"))
                    throw new InvalidOperationException("Media tool output is outside the qualified spending policy.");
            }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            var names = new HashSet<string>();
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate Responses field.");
                if (CountFields.Contains(property.Name))
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteRawValue(property.Value.GetRawText());
                }
                else if (!GenerationFields.Contains(property.Name))
                    throw new InvalidOperationException("Unqualified Responses field: " + property.Name);
            }
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsTransport) transport.Dispose();
        base.Dispose(disposing);
    }
}
