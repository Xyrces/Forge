using Microsoft.Extensions.Logging;

namespace PortHorizon.Agents.Acp;

public sealed class AcpSession
{
    private readonly AcpClient _client;
    private readonly ILogger<AcpSession>? _logger;

    public string SessionId { get; private set; }
    public string Cwd { get; }
    public string AgentName { get; }
    public AcpSessionState State { get; private set; } = AcpSessionState.Idle;
    public PromptResult? LastResponse { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public TimeSpan Elapsed => DateTime.UtcNow - StartedAt;

    public AcpSession(AcpClient client, string sessionId, string cwd, string agentName, ILogger<AcpSession>? logger = null)
    {
        _client = client;
        SessionId = sessionId;
        Cwd = cwd;
        AgentName = agentName;
        _logger = logger;
    }

    public async Task<PromptResult> PromptAsync(string message, CancellationToken cancellationToken)
    {
        State = AcpSessionState.Prompting;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.Token.Register(() =>
            {
                _ = SafeCancelAsync();
                State = AcpSessionState.Cancelled;
            });
            var result = await _client.PromptAsync(
                new PromptParams(SessionId, message), cancellationToken);
            LastResponse = result;
            State = AcpSessionState.Completed;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = AcpSessionState.Cancelled;
            throw;
        }
        catch
        {
            State = AcpSessionState.Failed;
            throw;
        }
    }

    private async Task SafeCancelAsync()
    {
        try
        {
            await _client.CancelAsync(new CancelParams(SessionId));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Cancel call failed for session {SessionId}", SessionId);
        }
    }
}
