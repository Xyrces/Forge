namespace Forge.Agents;

/// <summary>
/// Shared leaked-tool-call-markup detection + continuation nudge.
/// minimax-m3 (and siblings) occasionally emit a tool call as literal
/// text markup in the assistant content instead of structured
/// tool_calls, which ends the MAF loop prematurely. Both
/// <see cref="MafAgentRunner"/> (sessioned engineering runs) and
/// <see cref="PipelineAgentRunner"/> (single-shot pipeline runs)
/// detect the leak and nudge the model to re-issue properly.
/// Single source for the pattern list + prompt so the two loops
/// can never drift.
/// </summary>
internal static class LeakedToolCallMarkup
{
    /// <summary>
    /// Nudge sent when the model emits a tool call as plain-text
    /// markup. Deliberately short: the model has the full
    /// conversation in front of it already.
    /// </summary>
    internal const string ContinuationPrompt =
        "Your previous message contained a tool call emitted as plain-text markup, which cannot be executed. " +
        "If you intended to call a tool, re-issue it now as a proper tool call. " +
        "If you have already completed the task, reply with a brief summary of what you changed (no markup).";

    /// <summary>
    /// True when assistant text contains tool-call markup that leaked
    /// into the content channel instead of arriving as structured
    /// tool_calls.
    /// </summary>
    internal static bool IsPresent(string text) =>
        text.Contains("]<]minimax[>", StringComparison.Ordinal) ||
        text.Contains("<tool_call>", StringComparison.Ordinal) ||
        text.Contains("<invoke name=", StringComparison.Ordinal);
}
