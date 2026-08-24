using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Configuration for a single LLM provider. The factory matches a provider
/// by <see cref="Name"/> (e.g. "kilo-gateway", "openai", "anthropic") and
/// uses <see cref="BaseUrl"/> + <see cref="ApiKey"/> + <see cref="OrgId"/>
/// to construct the client. <see cref="DefaultModel"/> is used when a
/// role doesn't have an explicit entry in <see cref="LlmConfig.Roles"/>.
/// </summary>
public sealed record ProviderConfig(
    string Name,
    string BaseUrl,
    string? ApiKey,
    string? OrgId,
    string DefaultModel,
    // Wire protocol: null/"openai" = chat completions (the default);
    // "anthropic" = Anthropic Messages API (e.g. Kimi-for-Coding,
    // whose chat lives at {base}/messages with x-api-key auth — the
    // OpenAI-shaped /models listing works but chat 401s).
    string? Api = null,
    // Account-level quota shared across all models (Kimi): a quota
    // 429 cools the whole provider, not just the tripped model.
    bool SharedQuota = false,
    // Provider-level default max_tokens for Anthropic-protocol calls
    // (Kimi meters TPM on prompt + REQUESTED max_tokens). Null = 8192.
    int? MaxOutputTokens = null,
    // Effective input window in tokens, enabling intra-run context
    // compaction (operator-approved 2026-08-06 — task-560 died
    // mid-run at a 481KB transcript when minimax-m3's window
    // overflowed). When set, tool-loop runs wrap the client with a
    // ContextWindowCompactionStrategy reducer so accumulated tool
    // results are evicted/truncated before the request exceeds the
    // window. Null = no compaction (safe default when the provider's
    // true window is unknown — measure before guessing).
    int? ContextWindowTokens = null,
    // Anthropic-protocol auth scheme: "x-api-key" (default; Kimi)
    // or "bearer" (MiniMax /anthropic endpoint — subscription keys
    // authenticate as Authorization: Bearer). Ignored on the
    // OpenAI-protocol path (always Bearer there).
    string? Auth = null,
    // Explicit model-catalog URL for the Agents page dropdown.
    // Default: {BaseUrl}/models (OpenAI shape). Anthropic-protocol
    // providers whose chat base isn't the OpenAI-shaped root need
    // this — MiniMax chat is api.minimax.io/anthropic/v1 but its
    // model listing lives at api.minimax.io/v1/models.
    string? ModelsUrl = null);

/// <summary>
/// Per-role model assignment. Resolved by looking up
/// <see cref="LlmConfig.Roles"/>[<c>role</c>]; if no entry exists, the
/// default provider + default model is used.
/// </summary>
public sealed record RoleModel(
    string ProviderName,
    string Model);

/// <summary>
/// Provider configuration for the LLM runtime. Read from
/// <c>appsettings.json</c> and resolved once at orchestrator startup.
///
/// <para>
/// The shape supports multiple providers in parallel: a kilo gateway and
/// a fallback OpenAI account, for example. <see cref="DefaultProvider"/>
/// picks which one the orchestrator uses when a role doesn't have a
/// per-role entry in <see cref="Roles"/>.
/// </para>
/// </summary>
public sealed record LlmConfig(
    IReadOnlyList<ProviderConfig> Providers,
    string DefaultProvider,
    IReadOnlyDictionary<AgentType, RoleModel> Roles,
    // Per-role escalation models (phase 3): where a triage-escalated
    // run goes. Sits BELOW the DB escalation overrides
    // (llm/roleEscalationModel/...) in resolution; a role with no
    // entry and no override has NO escalation target.
    IReadOnlyDictionary<AgentType, RoleModel>? EscalationRoles = null)
{
    /// <summary>
    /// Convenience constructor for single-provider / no-role-dict configs
    /// (most tests). Equivalent to:
    /// <c>new LlmConfig([provider], provider.Name, new Dictionary&lt;AgentType, RoleModel&gt;())</c>.
    /// </summary>
    public LlmConfig(ProviderConfig provider)
        : this(new[] { provider }, provider.Name,
               new Dictionary<AgentType, RoleModel>()) { }

    /// <summary>
    /// Resolve the (provider, model) for a given role. Returns the default
    /// provider + its DefaultModel if the role has no explicit entry.
    /// Throws if the named provider is not in the Providers list.
    /// </summary>
    public (ProviderConfig Provider, string Model) Resolve(AgentType role)
    {
        if (Roles.TryGetValue(role, out var roleModel))
        {
            var provider = Providers.FirstOrDefault(p =>
                string.Equals(p.Name, roleModel.ProviderName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Role {role} references provider '{roleModel.ProviderName}' which is not in the Providers list. " +
                    $"Known providers: {string.Join(", ", Providers.Select(p => p.Name))}.");
            return (provider, roleModel.Model);
        }
        var defaultProvider = Providers.FirstOrDefault(p =>
            string.Equals(p.Name, DefaultProvider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"DefaultProvider '{DefaultProvider}' is not in the Providers list. " +
                $"Known providers: {string.Join(", ", Providers.Select(p => p.Name))}.");
        return (defaultProvider, defaultProvider.DefaultModel);
    }
}

public static class LlmProviders
{
    public const string Stub = "Stub";
    public const string OpenAI = "OpenAI";
    public const string Anthropic = "Anthropic";
    public const string GitHubCopilot = "GitHubCopilot";
    public const string Foundry = "Foundry";

    // Common OpenAI-compatible provider name. The kilo gateway uses this
    // identifier in appsettings.json; the OpenAICompatibleChatClientFactory
    // treats any non-Stub provider as OpenAI-compatible (POST /v1/chat/
    // completions, Bearer auth).
    public const string KiloGateway = "kilo-gateway";
}

/// <summary>
/// Adapter from <see cref="Configuration.LlmOptions"/> (appsettings.json
/// shape) to <see cref="LlmConfig"/> (runtime shape). The two shapes
/// differ because the config layer uses string-keyed dictionaries (for
/// the appsettings binder) while the runtime uses AgentType-keyed
/// dictionaries (for type safety).
/// </summary>
public static class LlmConfigAdapter
{
    public static LlmConfig FromOptions(Configuration.LlmOptions options)
    {
        var providers = options.Providers.Select(p => new ProviderConfig(
            Name: p.Name,
            BaseUrl: p.BaseUrl,
            ApiKey: string.IsNullOrEmpty(p.ApiKey) ? null : p.ApiKey,
            OrgId: string.IsNullOrEmpty(p.OrgId) ? null : p.OrgId,
            DefaultModel: p.DefaultModel,
            Api: string.IsNullOrEmpty(p.Api) ? null : p.Api,
            SharedQuota: p.SharedQuota,
            MaxOutputTokens: p.MaxOutputTokens > 0 ? p.MaxOutputTokens : null)).ToList();
        var roles = new Dictionary<AgentType, RoleModel>(capacity: options.Roles.Count);
        var escalationRoles = new Dictionary<AgentType, RoleModel>();
        foreach (var (key, value) in options.Roles)
        {
            if (!Enum.TryParse<AgentType>(key, ignoreCase: true, out var role))
            {
                throw new InvalidOperationException(
                    $"LlmOptions.Roles contains unknown role '{key}'. " +
                    $"Valid roles: {string.Join(", ", Enum.GetNames<AgentType>())}.");
            }
            roles[role] = new RoleModel(value.ProviderName, value.Model);
            if (value.EscalationModel is { } esc
                && !string.IsNullOrWhiteSpace(esc.ProviderName)
                && !string.IsNullOrWhiteSpace(esc.Model))
            {
                escalationRoles[role] = new RoleModel(esc.ProviderName, esc.Model);
            }
        }
        return new LlmConfig(providers, options.DefaultProvider, roles, escalationRoles);
    }
}
