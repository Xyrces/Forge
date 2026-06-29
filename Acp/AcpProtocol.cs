using System.Text.Json.Serialization;

namespace PortHorizon.Agents.Acp;

public interface IAcpClient : IAsyncDisposable
{
    Task<InitializeResult> InitializeAsync(InitializeParams @params, CancellationToken ct = default);
    Task<NewSessionResult> NewSessionAsync(NewSessionParams @params, CancellationToken ct = default);
    Task<PromptResult> PromptAsync(PromptParams @params, CancellationToken ct = default);
    Task CancelAsync(CancelParams @params, CancellationToken ct = default);
}

public sealed record InitializeParams(int ProtocolVersion, ClientCapabilities Capabilities);

public sealed record ClientCapabilities(
    bool Streaming = false,
    bool Cancellation = true,
    IReadOnlyList<string>? Roots = null);

public sealed record InitializeResult(string ServerName, string ServerVersion, int ProtocolVersion = 1);

public sealed record AcpSessionInfo(string SessionId, string? Cwd = null, string? AgentName = null);

public sealed record NewSessionParams(string Cwd, string AgentName);

public sealed record NewSessionResult(
    [property: JsonPropertyName("id")] string SessionId);

public sealed record PromptParams(string SessionId, string Message);

public sealed record PromptResult(
    [property: JsonPropertyName("content")] string Response,
    IReadOnlyList<ToolCallSummary>? ToolCalls = null);

public sealed record ToolCallSummary(string Name, bool Success, string? Detail = null);

public sealed record CancelParams(string SessionId);

public enum AcpSessionState
{
    Idle,
    Prompting,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// JSON message parts accepted by kilo's <c>POST /session/{id}/message</c>.
/// </summary>
public sealed record MessagePart(
    [property: JsonPropertyName("type")] string Type = "text",
    [property: JsonPropertyName("text")] string? Text = null);

/// <summary>
/// One message posted by the user to a session.
/// </summary>
public sealed record UserMessage(
    [property: JsonPropertyName("parts")] IReadOnlyList<MessagePart> Parts);
