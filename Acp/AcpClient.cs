using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace PortHorizon.Agents.Acp;

public sealed class AcpClient : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly HeaderDelimitedMessageHandler _handler;
    private readonly ILogger<AcpClient>? _logger;
    private bool _disposed;

    public AcpClient(TcpClient tcp, ILogger<AcpClient>? logger = null)
    {
        _tcp = tcp;
        _logger = logger;
        _stream = tcp.GetStream();
        _handler = new HeaderDelimitedMessageHandler(_stream, _stream, new JsonMessageFormatter());
        _rpc = new JsonRpc(_handler);
        _rpc.StartListening();
    }

    public Task<InitializeResult> InitializeAsync(InitializeParams @params, CancellationToken ct = default)
        => _rpc.InvokeWithCancellationAsync<InitializeResult>(
            "initialize", new object[] { @params }, ct);

    public Task<NewSessionResult> NewSessionAsync(NewSessionParams @params, CancellationToken ct = default)
        => _rpc.InvokeWithCancellationAsync<NewSessionResult>(
            "session/new", new object[] { @params }, ct);

    public Task<PromptResult> PromptAsync(PromptParams @params, CancellationToken ct = default)
        => _rpc.InvokeWithCancellationAsync<PromptResult>(
            "session/prompt", new object[] { @params }, ct);

    public Task CancelAsync(CancelParams @params, CancellationToken ct = default)
        => _rpc.InvokeWithCancellationAsync<object?>(
            "session/cancel", new object[] { @params }, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _rpc.Dispose(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "JsonRpc dispose"); }
        try { _stream.Dispose(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "NetworkStream dispose"); }
        try { _tcp.Dispose(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "TcpClient dispose"); }
    }
}
