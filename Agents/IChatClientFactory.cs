using Microsoft.Extensions.AI;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Builds an <see cref="IChatClient"/> from an <see cref="LlmConfig"/>.
/// Phase 0.5: <see cref="StubbedChatClientFactory"/> for tests; the real
/// <see cref="OpenAICompatibleChatClientFactory"/> covers OpenAI, Azure
/// OpenAI, and any other OpenAI-compatible HTTP endpoint (including the
/// kilo gateway).
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Build a chat client for the given role. The role is used to resolve
    /// which provider + model to use when <see cref="LlmConfig.Roles"/> is
    /// populated; otherwise the default provider + model is used.
    /// <paramref name="projectId"/> scopes DB model overrides: a
    /// project-scoped override wins over the global one (an override
    /// set for another project never applies).
    /// <paramref name="modelOverride"/> is the per-task explicit model
    /// (triage escalation, phase 3): when set it wins over EVERY other
    /// resolution tier for this one client. Throws
    /// <see cref="InvalidOperationException"/> when the override names
    /// an unconfigured provider — callers degrade to the normal
    /// resolution.
    /// </summary>
    IChatClient Create(LlmConfig config, AgentType role, string? projectId = null, RoleModel? modelOverride = null);
}
