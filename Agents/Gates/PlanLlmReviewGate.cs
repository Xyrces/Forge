using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Forge.Agents.Gates;

/// <summary>
/// The LLM critic: judges whether the plan SOLVES the task with a
/// sound, minimal, process-compliant approach. Runs on the reviewer
/// model config (one cheap call — the deterministic schema and
/// territory gates have already filtered malformed plans).
///
/// Critic errors fail OPEN (approve-with-warning): an LLM outage
/// must not brick the pipeline — the deterministic gates still
/// enforce structure and territory, and the failure is logged +
/// recorded in the gate audit trail.
/// </summary>
public sealed class PlanLlmReviewGate : IRunGate
{
    public const string GateName = "plan-llm-review";
    public string Name => GateName;
    public GateKind Kind => GateKind.Llm;
    public string Description => DescriptionText;

    /// <summary>User-facing description of this gate for the catalog.</summary>
    public const string DescriptionText =
        "Uses an LLM critic to judge whether the plan is sound, minimal, and process-compliant.";

    private readonly Func<IChatClient> _clientFactory;
    private readonly ILogger _logger;

    /// <summary>Hard cap on the critic call (2 min — the prompt is
    /// small and the deterministic gates front-run it).</summary>
    private static readonly TimeSpan CriticTimeout = TimeSpan.FromMinutes(2);

    public PlanLlmReviewGate(Func<IChatClient> clientFactory, ILogger logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<RunGateVerdict> EvaluateAsync(RunGateContext ctx)
    {
        var prompt = $$"""
            You are the plan-review gate for an autonomous engineering pipeline. A developer agent
            has explored the repository and submitted an implementation plan. Judge ONLY whether the
            plan is sound — the deterministic gates have already validated its structure and file
            territories.

            Evaluate:
            1. Does the approach actually solve the task as stated?
            2. Is it minimal — no unrelated restructuring, no scope creep?
            3. Does it respect process (tests required, no PR creation by the agent, no work outside
               the listed files)?
            4. Is the "done" evidence concrete and checkable?

            Reply with 2-6 sentences of feedback, then a final line EXACTLY one of:
            VERDICT: APPROVE
            VERDICT: REVISE

            ## Task
            {{ctx.TaskText}}

            ## Plan
            {{ctx.Plan}}
            """;
        string text;
        try
        {
            var client = _clientFactory();
            // Bounded call: the critic shares the reviewer model,
            // and a hung provider (observed live 2026-07-26: kimi-k3
            // connections open but never respond) must not eat the
            // agent run's own time budget. Timeout = fail open.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
            timeoutCts.CancelAfter(CriticTimeout);
            var response = await client.GetResponseAsync(prompt, cancellationToken: timeoutCts.Token);
            text = response.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlanLlmReviewGate: critic call failed — approving with warning");
            return new RunGateVerdict(GateOutcome.Approve,
                $"critic unavailable (approved with warning): {ex.GetType().Name}");
        }

        var verdictLine = text.Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase));
        if (verdictLine is not null && verdictLine.Contains("REVISE", StringComparison.OrdinalIgnoreCase))
        {
            var feedback = string.Join('\n', text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase))).Trim();
            return new RunGateVerdict(GateOutcome.Revise,
                $"Plan critic requests revision:\n{feedback}");
        }
        if (verdictLine is null)
        {
            _logger.LogWarning("PlanLlmReviewGate: critic returned no VERDICT line — approving with warning");
            return new RunGateVerdict(GateOutcome.Approve, "critic returned unparseable verdict (approved with warning)");
        }
        return RunGateVerdict.Approved;
    }
}
