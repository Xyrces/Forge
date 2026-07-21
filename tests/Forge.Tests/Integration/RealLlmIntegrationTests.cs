using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// P0.5 integration test: instantiate <see cref="OpenAICompatibleChatClientFactory"/>
/// from the <c>LLM_API_KEY</c> env var and run a single chat completion
/// against the configured endpoint. Skips cleanly when the env var is
/// absent (the default in CI and local dev).
///
/// <para>
/// To run locally: <c>$env:LLM_API_KEY = "..."; dotnet test --filter
/// FullyQualifiedName~RealLlmIntegration</c>. The factory will hit
/// <c>LLM_BASE_URL</c> (default <c>http://127.0.0.1:4096</c>, the local
/// kilo-gateway emulator default) using <c>LLM_MODEL</c> (default
/// <c>stub-model</c>).
/// </para>
///
/// <para>
/// P0.5 deliverable: the OpenAI-compatible chat client can reach the
/// kilo gateway, the response is non-empty, and the round-trip takes
/// under 30s. A real LLM in the loop unblocks every later phase
/// (P1.4 intake, P2 git tools, etc.) since the runner can now talk to
/// an actual model.
/// </para>
/// </summary>
public class RealLlmIntegrationTests
{
    private static (string? ApiKey, string BaseUrl, string Model) ResolveEnv()
    {
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://127.0.0.1:4096";
        var model = Environment.GetEnvironmentVariable("LLM_MODEL");
        if (string.IsNullOrEmpty(model)) model = "stub-model";
        return (apiKey, baseUrl, model);
    }

    [SkippableFact]
    public async Task OpenAICompatibleFactory_ResolvesKiloGateway_ReturnsNonEmptyResponse()
    {
        var (apiKey, baseUrl, model) = ResolveEnv();
        Skip.If(string.IsNullOrEmpty(apiKey),
            "LLM_API_KEY is not set; skipping real-LLM integration test. " +
            "Set LLM_API_KEY (and optionally LLM_BASE_URL, LLM_MODEL) to run this test.");

        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            OrgId: null,
            DefaultModel: model);
        var llmConfig = new LlmConfig(provider);
        using var factory = new OpenAICompatibleChatClientFactory();

        // The real call. We do NOT exercise MafAgentRunner here because it
        // constructs a ChatClientAgent with the role's instructions; for
        // the smoke test we just need the IChatClient to work end-to-end.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        var client = factory.Create(llmConfig, AgentType.CoreDev);
        var response = await client.GetResponseAsync(
            new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Reply with the single word: OK") },
            cancellationToken: cts.Token);
        sw.Stop();

        Assert.NotNull(response);
        Assert.NotEmpty(response.Messages);
        var text = string.Concat(response.Messages.Select(m => m.Text));
        Assert.False(string.IsNullOrWhiteSpace(text), $"Response text was empty after {sw.ElapsedMilliseconds}ms.");
        // The model should echo "OK" or close to it; we don't assert equality
        // because a real LLM might add whitespace or punctuation. The
        // contract is "non-empty + quick".
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"Round-trip took {sw.ElapsedMilliseconds}ms; expected < 30s.");
    }

    [SkippableFact]
    public async Task MafAgentRunner_RealLlm_EndToEnd()
    {
        var (apiKey, baseUrl, model) = ResolveEnv();
        Skip.If(string.IsNullOrEmpty(apiKey),
            "LLM_API_KEY is not set; skipping real-LLM MafAgentRunner test.");

        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            OrgId: null,
            DefaultModel: model);
        var llmConfig = new LlmConfig(provider);
        using var factory = new OpenAICompatibleChatClientFactory();
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: llmConfig,
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner.RunAsync(AgentType.CoreDev, "Reply with the single word: OK", sessionId: null, ct: cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }
}
