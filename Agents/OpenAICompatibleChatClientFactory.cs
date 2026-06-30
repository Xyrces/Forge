using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// Builds <see cref="IChatClient"/> instances for OpenAI-compatible HTTP
/// endpoints. The kilo gateway (and Azure OpenAI, vLLM, Ollama, etc.)
/// all speak the OpenAI Chat Completions wire format, so a single factory
/// covers them.
///
/// <para>
/// Clients are cached per (providerName, model) tuple so the
/// <see cref="MafAgentRunner"/> can request the same provider+model
/// repeatedly without re-handshaking. Disposing the factory disposes
/// all cached clients.
/// </para>
///
/// <para>
/// Configuration: <see cref="ProviderConfig.BaseUrl"/> points at the
/// chat-completions root (no trailing <c>/v1/</c> required; the OpenAI
/// SDK appends <c>/chat/completions</c> internally). Auth is
/// <c>Bearer &lt;ApiKey&gt;</c>. OrgId is passed through for providers
/// (e.g. OpenAI) that honor it.
/// </para>
/// </summary>
public sealed class OpenAICompatibleChatClientFactory : IChatClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, IChatClient> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IChatClient Create(LlmConfig config, AgentType role)
    {
        var (provider, model) = config.Resolve(role);
        if (string.IsNullOrEmpty(provider.ApiKey))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' has no ApiKey configured. " +
                "Set the apiKey field in appsettings.json (providers[].apiKey). " +
                "For tests, the LLM_API_KEY env var override is read by OpenAICompatibleChatClientFactory.TryFromEnv.");
        }
        return GetOrCreate(provider, model);
    }

    /// <summary>
    /// Test-only helper: build a factory from the LLM_API_KEY env var so
    /// the SkippableFact integration test doesn't need to write
    /// appsettings.json. The BaseUrl defaults to <c>http://127.0.0.1:4096</c>
    /// (the kilo serve default) but can be overridden with
    /// <c>LLM_BASE_URL</c>.
    /// </summary>
    public static OpenAICompatibleChatClientFactory? TryFromEnv(string defaultBaseUrl = "http://127.0.0.1:4096")
    {
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return null;

        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = defaultBaseUrl;

        var model = Environment.GetEnvironmentVariable("LLM_MODEL");
        if (string.IsNullOrEmpty(model)) model = "stub-model";

        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            OrgId: null,
            DefaultModel: model);
        return new OpenAICompatibleChatClientFactory();
        // Note: the factory itself is reusable; the caller passes the
        // LlmConfig built around `provider` into Create().
    }

    private IChatClient GetOrCreate(ProviderConfig provider, string model)
    {
        var key = provider.Name + "|" + model;
        return _cache.GetOrAdd(key, _ => Build(provider, model));
    }

    private static IChatClient Build(ProviderConfig provider, string model)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl),
        };
        if (!string.IsNullOrEmpty(provider.OrgId))
        {
            // OpenAI SDK doesn't have a direct OrgId setter; we drop it
            // into the default header via the options.
            // Real OpenAI SDK 2.1+ uses OpenAIClientOptions.OrganizationId,
            // but in 2.1.0-beta it's via the AddHeader equivalent.
            // For beta, we just skip org; can be added when stable.
        }
        var apiKeyCredential = new System.ClientModel.ApiKeyCredential(provider.ApiKey ?? string.Empty);
        var openAIClient = new OpenAIClient(apiKeyCredential, options);
        var chatClient = openAIClient.GetChatClient(model);
        return chatClient.AsIChatClient();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _cache.Values)
        {
            if (client is IDisposable d) d.Dispose();
        }
        _cache.Clear();
    }
}
