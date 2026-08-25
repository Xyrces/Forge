using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Forge.Agents;

/// <summary>The result of a <see cref="PipelineAgentRunner"/> run.</summary>
/// <param name="FinalResponse">The last <see cref="AgentResponse"/> —
/// only the messages added by that final round.</param>
/// <param name="NewMessages">Every message added across ALL rounds
/// (assistant + tool + nudge messages, in order). Callers inspecting
/// tool results (artifact ids, committed statuses) must scan this —
/// a contract-nudged final round does not carry earlier rounds'
/// <see cref="FunctionResultContent"/>s.</param>
/// <param name="LeakNudges">Leaked-markup nudges fired.</param>
/// <param name="ContractNudges">Required-tool nudges fired.</param>
/// <param name="BudgetExhausted">True when the continuation budget was
/// spent and the final round STILL violates (leak present or required
/// tool never called) — the caller's failure path should record a
/// text excerpt so the fizzle is diagnosable.</param>
public sealed record PipelineRunOutcome(
    AgentResponse FinalResponse,
    IReadOnlyList<ChatMessage> NewMessages,
    int LeakNudges,
    int ContractNudges,
    bool BudgetExhausted)
{
    public int TotalNudges => LeakNudges + ContractNudges;
}

/// <summary>
/// Single-shot pipeline runner: the shared continuation loop for the
/// pipeline agents (designer, artist, groomer, intake, triage,
/// product) that call <c>ChatClientAgent.RunAsync</c> directly and
/// therefore bypass <see cref="MafAgentRunner"/>'s leaked-markup
/// recovery. A single unlucky minimax-m3 markup leak otherwise ends
/// the run with prose as the "final answer" and zero tool calls —
/// observed live 2026-08-24 (designer runs 184/185 on epic-11
/// fizzled as "LLM completed without committing a spec status
/// transition" and retried every 15 minutes).
///
/// <para>Loop shape mirrors MafAgentRunner: run, accumulate the new
/// messages into the conversation, nudge (as a user message), re-run.
/// ONE shared continuation budget (max <see cref="MaxContinuations"/>)
/// across both nudge kinds:</para>
/// <list type="bullet">
///   <item>Leaked-markup nudge — assistant text contains a tool call
///   emitted as plain-text markup (<see cref="LeakedToolCallMarkup"/>).</item>
///   <item>Contract nudge (optional) — the round completed cleanly but
///   the caller's required tool was never called. Only callers whose
///   no-tool-call outcome is illegitimate pass one (the designer's
///   <c>db_set_spec_status</c> today); the nudge text must name every
///   legitimate exit so the model keeps a valid out.</item>
/// </list>
///
/// <para>Healthy runs are untouched: no nudge fires without a detected
/// violation, and the caller's post-run source-of-truth check (e.g.
/// re-fetching the committed spec status) remains the verdict — the
/// contract nudge is only the recovery.</para>
/// </summary>
public sealed class PipelineAgentRunner
{
    public const int MaxContinuations = 3;

    private readonly ILogger _logger;

    public PipelineAgentRunner(ILogger logger) => _logger = logger;

    /// <summary>
    /// Run <paramref name="agent"/> against the initial conversation
    /// (a single user prompt or a full caller-supplied history),
    /// nudging on leaked markup and (optionally) a missing required
    /// tool call until clean or the shared budget is spent.
    /// </summary>
    public async Task<PipelineRunOutcome> RunAsync(
        ChatClientAgent agent,
        IReadOnlyList<ChatMessage> initialMessages,
        string roleLabel,
        string? requiredToolName = null,
        string? contractNudgePrompt = null,
        CancellationToken ct = default)
    {
        var conversation = new List<ChatMessage>(initialMessages);
        var newMessages = new List<ChatMessage>();

        var response = await agent.RunAsync(conversation, cancellationToken: ct);
        conversation.AddRange(response.Messages);
        newMessages.AddRange(response.Messages);

        var leakNudges = 0;
        var contractNudges = 0;
        while (leakNudges + contractNudges < MaxContinuations)
        {
            string nudgeText;
            if (LeakedToolCallMarkup.IsPresent(LastAssistantText(response)))
            {
                leakNudges++;
                _logger.LogWarning(
                    "Role {Role}: tool-call markup leaked into response text; nudging model to continue ({N}/{Max})",
                    roleLabel, leakNudges + contractNudges, MaxContinuations);
                nudgeText = LeakedToolCallMarkup.ContinuationPrompt;
            }
            else if (requiredToolName is not null && !CalledTool(newMessages, requiredToolName))
            {
                contractNudges++;
                _logger.LogWarning(
                    "Role {Role}: required tool {Tool} was not called; nudging model to finish ({N}/{Max})",
                    roleLabel, requiredToolName, leakNudges + contractNudges, MaxContinuations);
                nudgeText = contractNudgePrompt
                    ?? $"You finished without calling {requiredToolName}. Call it now to complete the run.";
            }
            else
            {
                break;
            }

            var nudge = new ChatMessage(ChatRole.User, nudgeText);
            conversation.Add(nudge);
            newMessages.Add(nudge);
            response = await agent.RunAsync(conversation, cancellationToken: ct);
            conversation.AddRange(response.Messages);
            newMessages.AddRange(response.Messages);
        }

        var budgetExhausted = leakNudges + contractNudges >= MaxContinuations
            && (LeakedToolCallMarkup.IsPresent(LastAssistantText(response))
                || (requiredToolName is not null && !CalledTool(newMessages, requiredToolName)));
        if (budgetExhausted)
        {
            _logger.LogWarning(
                "Role {Role}: continuation budget exhausted ({Leak} leak, {Contract} contract nudges); run ends unresolved",
                roleLabel, leakNudges, contractNudges);
        }
        return new PipelineRunOutcome(response, newMessages, leakNudges, contractNudges, budgetExhausted);
    }

    private static bool CalledTool(IEnumerable<ChatMessage> messages, string toolName) =>
        messages.SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Any(c => string.Equals(c.Name, toolName, StringComparison.Ordinal));

    private static string LastAssistantText(AgentResponse response) =>
        response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? string.Empty;

    /// <summary>
    /// First <paramref name="max"/> chars of the last assistant text in
    /// the run — for failure-path error fields when a run ends
    /// unresolved, so a fizzle records what the model actually said
    /// (designer_run / artist_run carry no response-text column; the
    /// excerpt rides the existing error field).
    /// </summary>
    public static string FinalTextExcerpt(IReadOnlyList<ChatMessage> newMessages, int max = 500)
    {
        var text = newMessages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
