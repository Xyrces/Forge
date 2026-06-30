using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// Abstraction over the agent runtime. Phase 0: in-process Microsoft Agent
/// Framework (MAF) via <see cref="MafAgentRunner"/>. The kilo-based
/// <c>AcpClient</c> continues to work behind the same interface during the
/// P0..P3 rollback window.
///
/// Implementations are picked per the <c>Orchestrator:Runtime</c> config
/// flag (Kilo or Maf). The runner is stateless across tasks: each call
/// instantiates a fresh <c>AIAgent</c> (or, in P1.4, the intake path may
/// reuse a persistent <c>AgentSession</c>).
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Run a single agent invocation: build a role-appropriate agent, send
    /// the prompt, return the response text and a sessionId for restart
    /// safety (may be null for short-lived invocations).
    /// </summary>
    /// <param name="role">Which role agent to instantiate (CoreDev, ClientDev, QA, Reviewer).</param>
    /// <param name="prompt">The full prompt text including any system instructions, issue context, and operator message bus content.</param>
    /// <param name="sessionId">Optional session id to resume. Implementations may use it for restart safety (MAF: deserialize into <c>AgentSession</c>).</param>
    /// <param name="ct">Cancellation token. Honors SIGINT propagation.</param>
    Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, CancellationToken ct);
}
