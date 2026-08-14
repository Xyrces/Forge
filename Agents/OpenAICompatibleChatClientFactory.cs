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
    /// The provider the Headroom proxy fronts. The rewrite in
    /// <see cref="Create"/> applies ONLY to this provider — the
    /// proxy speaks OpenAI chat-completions to one upstream, so
    /// rewriting any other provider misroutes it (kimi's requests
    /// 401/404'd through the kilo-gateway proxy live).
    /// </summary>
    public string HeadroomProviderName { get; set; } = "kilo-gateway";

    /// <summary>True when the Headroom baseUrl rewrite applies to this
    /// provider: only the provider the proxy actually fronts.</summary>
    internal static bool ShouldRewriteForHeadroom(string providerName, string headroomProviderName) =>
        string.Equals(providerName, headroomProviderName, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Live per-role model overrides (dashboard Agents page). When a
    /// role has an override, <see cref="Create"/> resolves THAT
    /// provider+model instead of appsettings llm.roles / the default.
    /// Sync snapshot reads — set at startup by Program.cs.
    /// </summary>
    public RoleModelOverrides? Overrides { get; set; }

    /// <summary>
    /// Shared per-(provider, model) 429 cooldowns. When set, every
    /// cached client is wrapped in <see cref="RateLimitAwareChatClient"/>:
    /// fail-fast during cooldown, per-provider concurrency permit,
    /// centralized 429 recording. Set at startup by Program.cs.
    /// </summary>
    public Core.ModelRateLimitTracker? RateLimits { get; set; }

    /// <summary>
    /// Live provider-key snapshot (refreshed on a loop by Program.cs).
    /// When set, Create swaps in the freshest key for the resolved
    /// provider — a Secrets-page rotation takes effect on the next run
    /// without a restart. The client cache's key-hash then builds a
    /// fresh client; the stale one is never used again.
    /// </summary>
    public ProviderApiKeyResolver? KeyResolver { get; set; }

    /// <summary>Max simultaneous round-trips per provider (the
    /// "several concurrent agents" cap). Default 2; 0 disables the
    /// permit (cooldown tracking still applies).</summary>
    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>In-place retries for transient engine-overload 429s
    /// before a cooldown is recorded. Default 3; 0 disables.</summary>
    public int OverloadRetryCount { get; set; } = 3;

    /// <summary>Minimum interval between admitted requests PER
    /// PROVIDER (reserve-ahead pacing). Anti-herd: slots resuming
    /// after a shared cooldown leave spaced by this interval instead
    /// of in the same millisecond (MiniMax Token Plan dynamic
    /// throttling punishes the burst shape). Default 500ms;
    /// <see cref="TimeSpan.Zero"/> disables.</summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _permits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderPacer> _pacers = new(StringComparer.OrdinalIgnoreCase);

    public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null)
    {
        var (provider, model, _) = config.ResolveEffective(role, Overrides, projectId);
        // Live key first: a rotated secret must also rescue a config
        // whose placeholder was never boot-resolved.
        if (KeyResolver?.Get(provider.Name) is { Length: > 0 } freshKey)
            provider = provider with { ApiKey = freshKey };
        if (string.IsNullOrEmpty(provider.ApiKey))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' has no ApiKey configured. " +
                "Set the apiKey field in appsettings.json (providers[].apiKey). " +
                "For tests, the LLM_API_KEY env var override is read by OpenAICompatibleChatClientFactory.TryFromEnv.");
        }
        if (!string.IsNullOrEmpty(HeadroomProxyBaseUrl)
            && ShouldRewriteForHeadroom(provider.Name, HeadroomProviderName))
        {
            // Rewrite the baseUrl so the OpenAI client talks to
            // Headroom. The Headroom proxy is started with the
            // upstream URL as a CLI flag, so it knows where to
            // forward requests.
            provider = provider with { BaseUrl = HeadroomProxyBaseUrl };
        }
        var inner = GetOrCreate(provider, model, ThinkingBudgetFor(role));
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

    /// <summary>Extended-thinking budget (operator-approved
    /// 2026-08-01: Reviewer + CoreDev only, 4k tokens). Anthropic-api
    /// providers get the `thinking` request block; the reasoning
    /// lands in the run transcript as "model reasoning".</summary>
    public const int ThinkingBudgetTokens = 4000;

    /// <summary>Thinking is enabled where reading the model's
    /// reasoning has real diagnostic value (operator 2026-08-01):
    /// review verdicts, implementation choices, AND the planning-lane
    /// roles — intake's epic decomposition and the groomer's
    /// sprint-readiness judgments are the highest-leverage reasoning
    /// in the system ("probably moreso than most"). The groomer
    /// creates its client as AgentType.CoreDev, so it inherits the
    /// budget through that branch. Only meaningful for anthropic-api
    /// providers — Build ignores it elsewhere.</summary>
    private static int? ThinkingBudgetFor(AgentType role)
        => role is AgentType.CoreDev or AgentType.Reviewer or AgentType.Intake
            ? ThinkingBudgetTokens
            : null;

    private IChatClient GetOrCreate(ProviderConfig provider, string model, int? thinkingBudgetTokens)
    {
        // The cache key includes a short hash of the API key so a key
        // change (operator rotation via the Secrets page, or the
        // startup DB-secret substitution in Program.cs) never reuses
        // a client built with the old credential. The raw key is
        // never placed in the cache-key string.
        var keyHash = string.IsNullOrEmpty(provider.ApiKey)
            ? string.Empty
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(provider.ApiKey)))[..12];
        // The thinking budget rides the key too: CoreDev/Reviewer get
        // their own client instance with reasoning enabled. Auth
        // scheme likewise (a provider edit must not reuse a client
        // built with the old header).
        var key = provider.Name + "|" + model + "|" + keyHash + "|" + (thinkingBudgetTokens?.ToString() ?? "-")
            + "|" + (provider.Auth ?? "-");
        return _cache.GetOrAdd(key, _ => WrapForRateLimits(Build(provider, model, thinkingBudgetTokens), provider, model));
    }

    private IChatClient WrapForRateLimits(IChatClient inner, ProviderConfig provider, string model)
    {
        if (RateLimits is null && MaxConcurrentRequests <= 0) return inner;
        var permit = _permits.GetOrAdd(provider.Name,
            _ => new SemaphoreSlim(Math.Max(1, MaxConcurrentRequests)));
        var pacer = MinRequestInterval > TimeSpan.Zero
            ? _pacers.GetOrAdd(provider.Name, _ => new ProviderPacer(MinRequestInterval))
            : null;
        return new RateLimitAwareChatClient(inner, provider.Name, model,
            RateLimits ?? new Core.ModelRateLimitTracker(), permit,
            provider.SharedQuota, OverloadRetryCount, pacer: pacer);
    }

    private static IChatClient Build(ProviderConfig provider, string model, int? thinkingBudgetTokens = null)
    {
        if (string.Equals(provider.Api, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            // Anthropic Messages protocol (Kimi-for-Coding): chat at
            // {base}/messages, x-api-key auth.
            return new AnthropicMessagesChatClient(provider.BaseUrl, provider.ApiKey ?? string.Empty, model,
                defaultMaxOutputTokens: provider.MaxOutputTokens ?? 8192,
                thinkingBudgetTokens: thinkingBudgetTokens,
                authScheme: provider.Auth ?? AnthropicMessagesChatClient.AuthSchemeXApiKey);
        }

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl),
            // Bump the default 100s network timeout. The agent's
            // tool-call loops (multiple bash iterations + final
            // assistant response) routinely exceed 100s when running
            // against the kilo gateway, and the default timeout
            // throws AggregateException mid-run which the runner
            // then surfaces as a malformed <threw:> response.
            NetworkTimeout = TimeSpan.FromMinutes(5),
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
