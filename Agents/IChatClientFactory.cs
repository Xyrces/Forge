using Microsoft.Extensions.AI;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// Builds an <see cref="IChatClient"/> from an <see cref="LlmConfig"/>.
/// Phase 0: <see cref="StubbedChatClientFactory"/> for tests; real
/// providers land in P0.5+.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(LlmConfig config);
}
