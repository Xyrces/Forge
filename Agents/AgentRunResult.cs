namespace Forge.Agents;

/// <summary>
/// What <see cref="IAgentRunner.RunAsync"/> returns. Captures the response
/// text plus enough metadata to (a) persist the model response to the
/// issue's metadata, (b) detect empty responses (no tool calls, no text)
/// so the orchestrator can mark the issue Completed with no diff, and
/// (c) feed the dashboard.
///
/// <para>
/// <see cref="SessionId"/> is a REFERENCE to the run's persisted
/// session — the memory-store key the session JSON lives under
/// (<c>session/&lt;project&gt;/&lt;task&gt;/&lt;role&gt;</c>), not the
/// blob itself. The runner resumes that session automatically on the
/// next run of the same task+role (pause/resume rework loop); callers
/// pass <c>sessionId: null</c> and the runner resolves the rest.
/// </para>
/// </summary>
public sealed record AgentRunResult(
    string Text,
    string? SessionId,
    long InputTokens,
    long OutputTokens,
    TimeSpan Elapsed);

