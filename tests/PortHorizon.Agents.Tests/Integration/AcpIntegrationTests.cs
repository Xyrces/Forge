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
        Assert.StartsWith("ses_", result.SessionId);
    }

    [SkippableFact]
    public async Task Prompt_RealModel_ReturnsNonEmptyResponse()
    {
        SkipIfUnavailable();
        await using var client = await _fx.ConnectAsync();
        await client.InitializeAsync(new InitializeParams(1, new ClientCapabilities()));
        var session = await client.NewSessionAsync(
            new NewSessionParams("C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon", ""));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await client.PromptAsync(
            new PromptParams(session.SessionId, "Reply with the single word: OK"),
            cts.Token);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Response));
    }

    [SkippableFact]
    public async Task Cancel_DoesNotThrow()
    {
        SkipIfUnavailable();
        await using var client = await _fx.ConnectAsync();
        await client.InitializeAsync(new InitializeParams(1, new ClientCapabilities()));
        var session = await client.NewSessionAsync(
            new NewSessionParams("C:\\Users\\jtn50\\repos\\gamedev\\PortHorizon", ""));

        await client.CancelAsync(new CancelParams(session.SessionId));
    }
}


