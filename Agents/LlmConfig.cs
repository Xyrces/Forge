namespace PortHorizon.Agents.Agents;

/// <summary>
/// Provider configuration for the LLM runtime. Read from
/// <c>appsettings.json</c> (or environment overrides) and resolved once at
/// orchestrator startup.
///
/// <para>
/// Phase 0: <see cref="Provider"/> is <c>Stub</c> in tests (no real network)
/// and <c>OpenAI</c> / <c>Anthropic</c> / <c>GitHubCopilot</c> behind the
/// <c>Orchestrator:Runtime=Maf</c> feature flag. P0.5+ phases add real
/// provider implementations.
/// </para>
/// </summary>
public sealed record LlmConfig(
    string Provider,
    string Model,
    string? ApiKey,
    string? OrgId);

public static class LlmProviders
{
    public const string Stub = "Stub";
    public const string OpenAI = "OpenAI";
    public const string Anthropic = "Anthropic";
    public const string GitHubCopilot = "GitHubCopilot";
    public const string Foundry = "Foundry";
}
