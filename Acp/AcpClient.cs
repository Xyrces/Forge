using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PortHorizon.Agents.Acp;

/// <summary>
/// HTTP+JSON client for kilo's REST API.
///
/// kilo v7.3.54 implements its own HTTP+JSON message API rather than the
/// upstream Agent Client Protocol's StreamJsonRpc-over-TCP. This class is a
/// thin wrapper around <see cref="HttpClient"/>.
///
/// Wire-level behavior for <see cref="PromptAsync"/>:
///
///   1. POST /session/{id}/message with the user message. The response is
///      the *initial* assistant turn — but on this build it is the agent's
///      first step only; if the agent decides to make more tool calls or
///      keep thinking, the response stream is NOT closed. We register the
///      prompt and then start polling.
///
///   2. Poll GET /session/{id} every second. The session JSON has
///      {summary:{additions,deletions,files}, time:{created,updated}}. The
///      agent is considered "done" when:
///        - we have observed at least one assistant turn (output tokens > 0
///          OR summary changes), AND
///        - time.updated is stable for <see cref="CompletionStableSeconds"/>
///          seconds (default 5).
///
///   3. To harvest the final assistant text, fetch GET /session/{id}/messages
///      (an SSE stream of NDJSON-like events) and keep reading until we see a
///      "session.idle" / completion marker, or for up to 5 s. We concatenate
///      any text parts found.
///
///   4. Hard cap: <see cref="HardTimeoutSeconds"/> (default 480). On cap,
///      we return whatever we have so far with a synthetic note in toolCalls.
///
/// Endpoints:
///   GET  /global/health                 -> liveness probe (used by AcpProcessManager)
///   POST /session                       -> { "id": "ses_*" }   (create session)
///   POST /session/{id}/message          -> { "info": ..., "parts": [...] }  (prompt)
///   GET  /session/{id}                  -> session metadata (poll target)
///   GET  /session/{id}/messages         -> SSE event stream (text harvest)
/// </summary>
public sealed class AcpClient : IAcpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<AcpClient>? _logger;
    private bool _disposed;

    public static int CompletionStableSeconds { get; set; } = 5;
    public static int HardTimeoutSeconds { get; set; } = 480;
    public static int InitialPollDelayMs { get; set; } = 800;

    public AcpClient(HttpClient http, ILogger<AcpClient>? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<InitializeResult> InitializeAsync(InitializeParams @params, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("global/health", ct);
        resp.EnsureSuccessStatusCode();
        return new InitializeResult("kilo-serve", $"http/{resp.Version}");
    }

    public async Task<NewSessionResult> NewSessionAsync(NewSessionParams @params, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("session", content: null, ct);
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<KiloSession>(JsonOptions, ct);
        if (created is null || string.IsNullOrEmpty(created.Id))
            throw new InvalidOperationException("kilo /session returned no id");
        return new NewSessionResult(created.Id);
    }

    public async Task<PromptResult> PromptAsync(PromptParams @params, CancellationToken ct = default)
    {
        var msg = new UserMessage(new[] { new MessagePart("text", @params.Message) });

        // The POST /session/{id}/message request hangs (kilo doesn't close the
        // response stream until the agent fully finishes). So we run two things
        // concurrently:
        //   - the POST itself, with a long timeout
        //   - a poller that watches GET /session/{id} and cancels the POST as
        //     soon as the session is stable for CompletionStableSeconds
        //
        // This gives us prompt completion detection in ~5–30 s instead of 5 min
        // hard timeout, and we still get the assistant's first-turn text from
        // the POST response if it ever arrives.

        using var postCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        postCts.CancelAfter(TimeSpan.FromSeconds(HardTimeoutSeconds));
        var postTask = PostMessageAsync(@params.SessionId, msg, postCts.Token);

        var pollTask = PollUntilStableAsync(@params.SessionId, postCts, ct);

        KiloMessageResponse? initial = null;
        string initialText = string.Empty;
        var initialTools = new List<ToolCallSummary>();
        try
        {
            initial = await postTask;
            initialText = ExtractText(initial);
            initialTools = ExtractTools(initial);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Post was canceled because the poller detected completion, or the
            // hard timeout fired. Either way, the session is ready; we'll fall
            // through to harvest.
            _logger?.LogDebug("POST /session/{Sid}/message canceled; session considered complete", @params.SessionId);
        }

        // Make sure the poller is fully done before we read final state.
        try { await pollTask; } catch { /* poll already canceled or completed */ }

        // Step 3: harvest final text by reading the SSE messages stream (best-effort).
        string finalText = initialText;
        var finalTools = initialTools;
        try
        {
            using var harvestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            harvestCts.CancelAfter(TimeSpan.FromSeconds(5));
            var harvested = await HarvestMessagesAsync(@params.SessionId, TimeSpan.FromSeconds(5), harvestCts.Token);
            if (!string.IsNullOrEmpty(harvested.Text)) finalText = harvested.Text;
            if (harvested.Tools.Count > 0) finalTools = harvested.Tools;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "harvest failed for {Sid}; using initial response text", @params.SessionId);
        }

        return new PromptResult(finalText, finalTools);
    }

    private async Task<KiloMessageResponse> PostMessageAsync(string sessionId, UserMessage msg, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"session/{sessionId}/message", msg, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<KiloMessageResponse>(JsonOptions, ct)
            ?? new KiloMessageResponse();
    }

    private async Task PollUntilStableAsync(string sessionId, CancellationTokenSource cancelPost, CancellationToken outerCt)
    {
        await Task.Delay(InitialPollDelayMs, outerCt);
        var deadline = DateTime.UtcNow.AddSeconds(HardTimeoutSeconds);
        long lastUpdated = 0;
        var stableSince = DateTime.UtcNow;
        var firstObservation = true;
        long observedOutputTokens = 0;

        while (DateTime.UtcNow < deadline && !outerCt.IsCancellationRequested)
        {
            KiloSessionFull? snap;
            try { snap = await GetSessionAsync(sessionId, outerCt); }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "session poll failed for {Sid}", sessionId);
                await Task.Delay(1000, outerCt);
                continue;
            }
            if (snap is null)
            {
                await Task.Delay(1000, outerCt);
                continue;
            }

            observedOutputTokens = Math.Max(observedOutputTokens, snap.Tokens?.Output ?? 0L);
            var updated = snap.Time?.Updated ?? 0L;
            if (firstObservation)
            {
                firstObservation = false;
                lastUpdated = updated;
                stableSince = DateTime.UtcNow;
            }
            else if (updated != lastUpdated)
            {
                lastUpdated = updated;
                stableSince = DateTime.UtcNow;
            }
            else if (observedOutputTokens > 0 &&
                     (DateTime.UtcNow - stableSince).TotalSeconds >= CompletionStableSeconds)
            {
                _logger?.LogInformation("session {Sid} stable for {Ss}s after observed output",
                    sessionId, CompletionStableSeconds);
                cancelPost.Cancel(); // tell the POST to give up
                return;
            }

            await Task.Delay(1000, outerCt);
        }
        cancelPost.Cancel();
    }

    public Task CancelAsync(CancelParams @params, CancellationToken ct = default)
    {
        // kilo v7.3.54 has no documented cancel endpoint; best-effort no-op.
        _logger?.LogDebug("Cancel requested for session {SessionId}; no-op (kilo v7.x)", @params.SessionId);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private async Task<KiloMessageResponse> PostWithTimeoutAsync(string sessionId, UserMessage msg, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await _http.PostAsJsonAsync($"session/{sessionId}/message", msg, JsonOptions, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<KiloMessageResponse>(JsonOptions, cts.Token)
            ?? new KiloMessageResponse();
    }

    private async Task<KiloSessionFull?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"session/{sessionId}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<KiloSessionFull>(JsonOptions, ct);
    }

    private async Task<(string Text, List<ToolCallSummary> Tools)> HarvestMessagesAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await _http.GetAsync($"session/{sessionId}/messages", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode) return (string.Empty, new());

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sb = new StringBuilder();
        var tools = new List<ToolCallSummary>();
        var deadline = DateTime.UtcNow + timeout;
        while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.Length == 0) continue;
            var parsed = TryParseMessageLine(line);
            if (parsed is null) continue;
            if (parsed.Type == "text" && !string.IsNullOrEmpty(parsed.Text))
                sb.AppendLine(parsed.Text);
            else if (!string.IsNullOrEmpty(parsed.Type) && parsed.Type != "text" && parsed.Type != "step-start" && parsed.Type != "step-finish")
                tools.Add(new ToolCallSummary(parsed.Type, true, parsed.Text));
        }
        return (sb.ToString().TrimEnd(), tools);
    }

    private static MessagePart? TryParseMessageLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var text = root.TryGetProperty("text", out var x) ? x.GetString() : null;
            return new MessagePart(type ?? "unknown", text);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractText(KiloMessageResponse body)
    {
        if (body.Parts is null) return string.Empty;
        return string.Concat(body.Parts
            .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text ?? ""));
    }

    private static List<ToolCallSummary> ExtractTools(KiloMessageResponse body)
    {
        if (body.Parts is null) return new();
        return body.Parts
            .Where(p => !string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(p => new ToolCallSummary(p.Type, true, p.Text))
            .ToList();
    }

    private sealed class KiloSession
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("directory")] public string? Directory { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    private sealed class KiloMessageResponse
    {
        [JsonPropertyName("info")] public object? Info { get; set; }
        [JsonPropertyName("parts")] public List<KiloPart>? Parts { get; set; }
    }

    private sealed class KiloPart
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class KiloSessionFull
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public SummaryBlock? Summary { get; set; }
        [JsonPropertyName("tokens")] public TokensBlock? Tokens { get; set; }
        [JsonPropertyName("time")] public TimeBlock? Time { get; set; }
    }

    private sealed class SummaryBlock
    {
        [JsonPropertyName("additions")] public int Additions { get; set; }
        [JsonPropertyName("deletions")] public int Deletions { get; set; }
        [JsonPropertyName("files")] public int Files { get; set; }
    }

    private sealed class TokensBlock
    {
        [JsonPropertyName("input")] public long Input { get; set; }
        [JsonPropertyName("output")] public long Output { get; set; }
    }

    private sealed class TimeBlock
    {
        [JsonPropertyName("created")] public long Created { get; set; }
        [JsonPropertyName("updated")] public long Updated { get; set; }
    }
}