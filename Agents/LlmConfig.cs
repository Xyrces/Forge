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
    string? Api = null);

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
    IReadOnlyDictionary<AgentType, RoleModel> Roles)
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
            DefaultModel: p.DefaultModel)).ToList();
        var roles = new Dictionary<AgentType, RoleModel>(capacity: options.Roles.Count);
        foreach (var (key, value) in options.Roles)
        {
            if (!Enum.TryParse<AgentType>(key, ignoreCase: true, out var role))
            {
                throw new InvalidOperationException(
                    $"LlmOptions.Roles contains unknown role '{key}'. " +
                    $"Valid roles: {string.Join(", ", Enum.GetNames<AgentType>())}.");
            }
            roles[role] = new RoleModel(value.ProviderName, value.Model);
        }
        return new LlmConfig(providers, options.DefaultProvider, roles);
    }
}
