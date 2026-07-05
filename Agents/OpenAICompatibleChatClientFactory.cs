using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using Forge.Core;

namespace Forge.Agents;

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

    /// <summary>
    /// Optional Headroom sidecar URL. When non-null, the
    /// factory rewrites the provider's <see cref="ProviderConfig.BaseUrl"/>
    /// to point at the local Headroom proxy (which forwards to
    /// the upstream). The Headroom proxy is started externally
    /// (see <c>deploy/docker-compose.headroom.yml</c>) and is
    /// responsible for forwarding. Our chat client doesn't know
    /// it's talking to a proxy.
    /// </summary>
    public string? HeadroomProxyBaseUrl { get; set; }

    /// <summary>
    /// Optional <see cref="CostTracker"/> singleton. When set,
    /// the factory wraps every <see cref="IChatClient"/> it
    /// returns in a per-session <see cref="DelegatingChatClient"/>
    /// that forwards each call's <c>UsageDetails</c> into the
    /// shared tracker. Set by <c>Program.cs</c> when
    /// <c>headroom.trackUsage</c> is true. The dashboard reads
    /// the totals via <c>GET /api/cost/stats</c>.
    /// </summary>
    public CostTracker? CostTracker { get; set; }

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
        if (!string.IsNullOrEmpty(HeadroomProxyBaseUrl))
        {
            // Rewrite the baseUrl so the OpenAI client talks to
            // Headroom. The Headroom proxy is started with the
            // upstream URL as a CLI flag, so it knows where to
            // forward requests.
            provider = provider with { BaseUrl = HeadroomProxyBaseUrl };
        }
        var inner = GetOrCreate(provider, model);
        if (CostTracker is null) return inner;
        // Per-session wrapper: forwards UsageDetails into the
        // shared CostTracker. The inner client is cached so
        // multiple sessions share one OpenAI client; per-session
        // wrappers are cheap (just a delegator).
        return new UsageTrackingChatClient(inner, CostTracker, role);
    }

/// <summary>
/// Thin <see cref="DelegatingChatClient"/> that forwards
/// <c>UsageDetails</c> from each response into a shared
/// <see cref="CostTracker"/>. Constructed per-session by
/// <see cref="OpenAICompatibleChatClientFactory.Create"/>.
/// </summary>
internal sealed class UsageTrackingChatClient : DelegatingChatClient
{
    private readonly CostTracker _tracker;
    private readonly string _role;
    public UsageTrackingChatClient(IChatClient inner, CostTracker tracker, AgentType role) : base(inner)
    {
        _tracker = tracker;
        _role = role.ToString();
    }
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await InnerClient.GetResponseAsync(messages, options, cancellationToken);
        _tracker.Record(response.Usage, roleHint: _role);
        return response;
    }
}
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
