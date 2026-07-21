namespace Forge.Agents;

/// <summary>
/// What <see cref="IAgentRunner.RunAsync"/> returns. Captures the response
/// text plus enough metadata to (a) persist the model response to the
/// issue's metadata, (b) detect empty responses (no tool calls, no text)
/// so the orchestrator can mark the issue Completed with no diff, and
/// (c) feed the dashboard.
///
/// <para>
/// <see cref="SessionId"/> is the MAF <c>AgentSession</c> blob in
/// <c>JsonElement</c> form. Round-trip it via
/// <c>AgentSession.SerializeSessionAsync</c> / <c>DeserializeSessionAsync</c>
/// to resume the conversation after a restart. The pre-MAF ACP runner
/// returned null here (it had no session equivalent).
/// </para>
/// </summary>
public sealed record AgentRunResult(
    string Text,
    string? SessionId,
    int InputTokens,
    int OutputTokens,
    TimeSpan Elapsed);

