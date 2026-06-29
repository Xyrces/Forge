namespace PortHorizon.Agents.Acp;

public record InitializeParams(int ProtocolVersion, ClientCapabilities Capabilities);
public record InitializeResult(string ServerName, string ServerVersion, int ProtocolVersion);

public record ClientCapabilities(
    bool Streaming = false,
    bool Cancellation = true,
    IReadOnlyList<string>? Roots = null);

public record AcpSessionInfo(string SessionId, string? Cwd = null, string? AgentName = null);

public record NewSessionParams(string Cwd, string AgentName);
public record NewSessionResult(string SessionId);

public record PromptParams(string SessionId, string Message);
public record PromptResult(string Response, IReadOnlyList<ToolCallSummary>? ToolCalls = null);

public record ToolCallSummary(string Name, bool Success, string? Detail = null);

public record CancelParams(string SessionId);

public enum AcpSessionState
{
    Idle,
    Prompting,
    Completed,
    Failed,
    Cancelled
}
