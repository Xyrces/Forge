namespace Forge.Agents;

/// <summary>
/// What <see cref="IAgentRunner.RunAsync"/> returns. Captures the response
/// text plus enough metadata to (a) persist the model response to the
/// issue's metadata, (b) detect empty responses (no tool calls, no text)
/// so the orchestrator can mark the issue Completed with no diff, and
/// (c) feed the dashboard.
///
/// <para>
/// <see cref="SessionId"/> identifies the stored conversation for this project, task and role.
/// Reviewer keys also include the reviewed commit. Callers can pass <c>sessionId: null</c>
/// to resume the conversation associated with the current run.
/// </para>
/// </summary>
public sealed record AgentRunResult(
    string Text,
    string? SessionId,
    long InputTokens,
    long OutputTokens,
    TimeSpan Elapsed,
    Core.VerificationReceipt? Verification = null,
    Core.Execution.WorkerPhaseTiming? Phases = null);

