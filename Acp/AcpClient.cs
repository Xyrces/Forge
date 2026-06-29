using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PortHorizon.Agents.Acp;

/// <summary>
/// HTTP+JSON client for kilo's REST API. kilo v7.3.54 implements its own
/// HTTP+JSON message API rather than the upstream Agent Client Protocol's
/// StreamJsonRpc-over-TCP, so this class is a thin wrapper around <see cref="HttpClient"/>.
///
/// Endpoints used:
///   GET  /global/health                 -> liveness probe (used by AcpProcessManager)
///   POST /session                       -> { "id": "ses_*" }   (create session)
///   POST /session/{id}/message          -> { "info": ..., "parts": [...] }  (prompt)
///   GET  /session/{id}                  -> session metadata
///
/// We do not call /initialize; kilo creates sessions implicitly. The "Initialize"
/// step here is a no-op that returns a synthetic result used by the orchestrator
/// just to confirm the server is up.
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
        using var resp = await _http.PostAsJsonAsync($"session/{@params.SessionId}/message", msg, JsonOptions, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<KiloMessageResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("kilo /session/<id>/message returned empty");

        // Each part is { type, text? }. Concatenate text parts.
        var parts = body.Parts ?? new List<KiloPart>();
        var text = string.Concat(parts
            .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text ?? ""));

        var tools = parts
            .Where(p => !string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(p => new ToolCallSummary(p.Type, true, p.Text))
            .ToList();

        return new PromptResult(text, tools);
    }

    public Task CancelAsync(CancelParams @params, CancellationToken ct = default)
    {
        // kilo does not document a cancel endpoint in v7.3.54; we treat this as best-effort.
        _logger?.LogDebug("Cancel requested for session {SessionId}; no-op (kilo v7.x)", @params.SessionId);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    // Kilo's actual response shape for /session.
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

}
