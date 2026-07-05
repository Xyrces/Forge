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
    /// </summary>
    IChatClient Create(LlmConfig config, AgentType role);
}
