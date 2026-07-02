using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// Abstraction over the agent runtime. Phase 0: in-process Microsoft Agent
/// Framework (MAF) via <see cref="MafAgentRunner"/>. The kilo/ACP path was
/// removed in P0 once the MAF runner proved the contract.
///
/// <para>
/// The runner is stateless across tasks: each call instantiates a fresh
/// <c>AIAgent</c> (or, in P1.4, the intake path may reuse a persistent
/// <c>AgentSession</c>).
/// </para>
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
    /// <param name="context">Optional run context (worktree path, branch, etc.). Implementations use this to bind tools (e.g. <c>bash</c>'s working directory) to the per-task environment.</param>
    /// <param name="ct">Cancellation token. Honors SIGINT propagation.</param>
    Task<AgentRunResult> RunAsync(
        AgentType role,
        string prompt,
        string? sessionId,
        IReadOnlyDictionary<string, object>? context,
        CancellationToken ct);

    /// <summary>
    /// Convenience overload for callers that don't have a context dict.
    /// Equivalent to passing <c>context: null</c>.
    /// </summary>
    Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, CancellationToken ct)
        => RunAsync(role, prompt, sessionId, context: null, ct);
}
