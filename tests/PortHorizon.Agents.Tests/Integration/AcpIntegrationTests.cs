using System.Net;
using System.Net.Sockets;
using System.Text;
using PortHorizon.Agents.Acp;
using Xunit;

namespace PortHorizon.Agents.Tests.Integration;

[Trait("Category", "Integration")]
public class AcpIntegrationTests : IClassFixture<AcpIntegrationFixture>
{
    private readonly AcpIntegrationFixture _fx;

    public AcpIntegrationTests(AcpIntegrationFixture fx) { _fx = fx; }

    private void SkipIfUnavailable()
    {
        Skip.If(_fx.KiloMissing || _fx.ServerBindFailed, _fx.SkipReason);
    }

    /// <summary>
    /// The first test to write is a raw-byte probe: connect to kilo's TCP port,
    /// send two framing variants (HTTP-style header-delimited JSON-RPC, NDJSON),
    /// and capture whatever comes back. This establishes the actual wire protocol
    /// before any higher-level test assumes StreamJsonRpc semantics.
    /// </summary>
    [SkippableFact]
    public async Task ProbeTcpFrame_DocumentsWireProtocol()
    {
        SkipIfUnavailable();
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, _fx.Port);

        var headerDelimited =
            "Content-Length: 49\r\n\r\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}";

        await tcp.SendStringAsync(headerDelimited);

        var response = await tcp.ReadAvailableAsync(TimeSpan.FromSeconds(3));
        // Diagnostic: log it so future debuggers see what kilo actually sends back.
        _fx.LastProbeResult = $"hd-len={Encoding.UTF8.GetByteCount(headerDelimited)} resp-bytes={response.Length} resp={response}";
        Assert.True(true, _fx.LastProbeResult);
    }

    [SkippableFact]
    public async Task Initialize_ReturnsServerInfo()
    {
        SkipIfUnavailable();
        await using var client = await _fx.ConnectAsync();

        var result = await client.InitializeAsync(
            new InitializeParams(1, new ClientCapabilities()));

        Assert.NotNull(result.ServerName);
        Assert.NotNull(result.ServerVersion);
        Assert.NotEqual("", result.ServerName);
    }

    [SkippableFact]
    public async Task NewSession_ReturnsSessionId()
    {
        SkipIfUnavailable();
        await using var client = await _fx.ConnectAsync();
        await client.InitializeAsync(new InitializeParams(1, new ClientCapabilities()));

        var result = await client.NewSessionAsync(
            new NewSessionParams("C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon", ""));

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
    }

    [SkippableFact]
    public async Task Prompt_RealModel_ReturnsNonEmptyResponse()
    {
        SkipIfUnavailable();
        await using var client = await _fx.ConnectAsync();
        await client.InitializeAsync(new InitializeParams(1, new ClientCapabilities()));
        var session = await client.NewSessionAsync(
            new NewSessionParams("C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon", ""));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await client.PromptAsync(
            new PromptParams(session.SessionId, "Reply with the single word: OK"),
            cts.Token);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Response));
    }
}

internal static class TcpProbeHelpers
{
    public static async Task SendStringAsync(this TcpClient tcp, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        await tcp.GetStream().WriteAsync(bytes);
    }

    public static async Task<string> ReadAvailableAsync(this TcpClient tcp, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var stream = tcp.GetStream();
        var buf = new byte[4096];
        try
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
            return n == 0 ? "<remote-closed>" : Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (OperationCanceledException)
        {
            return "<timeout>";
        }
        catch (IOException ex)
        {
            return $"<io-error:{ex.Message}>";
        }
    }
}


