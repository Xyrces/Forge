using System.ComponentModel;
using Forge.Agents.Gates;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Forge.AgentTools;

/// <summary>
/// The plan-gate tool: the agent explores the worktree, then submits
/// a structured implementation plan. The tool runs the run-gate
/// pipeline and the RESULT is the verdict — approved plans unlock
/// the mutating tools (the bash tool refuses mutations until then),
/// revise verdicts come back with concrete feedback (bounded by
/// <see cref="RunGateState.MaxRevisions"/>), and the agent continues
/// in the SAME session with full context.
/// </summary>
public sealed class SubmitPlanTool
{
    private readonly RunGateState _state;
    private readonly RunGatePipeline _pipeline;
    private readonly RunGateContext _baseContext;
    private readonly ILogger? _logger;

    public SubmitPlanTool(
        RunGateState state,
        RunGatePipeline pipeline,
        RunGateContext baseContext,
        ILogger? logger = null)
    {
        _state = state;
        _pipeline = pipeline;
        _baseContext = baseContext;
        _logger = logger;
    }

    [Description("Submit your structured implementation plan for approval. REQUIRED before any file edit, git mutation, or other state-changing command. The plan must have these sections: goal (restated in your words), files (concrete paths, mark creations \"(new)\"), approach, test (how you prove it), done (checkable completion evidence). Returns APPROVED (proceed to implementation) or REVISE with feedback.")]
    public async Task<string> SubmitPlan(
        [Description("The full structured plan text.")] string plan,
        CancellationToken ct = default)
    {
        if (_state.PlanApproved)
        {
            return "PLAN ALREADY APPROVED — proceed with implementation.";
        }
        if (_state.PlanFailed)
        {
            return "PLAN REJECTED (final) — the revision budget is exhausted. Report the failure and stop.";
        }
        if (_state.FastPath)
        {
            _state.PlanApproved = true;
            _state.PlanText = plan;
            _state.Verdicts.Add(("fast-path", GateOutcome.Approve, "mechanical round — rework context prescribes the steps"));
            _logger?.LogInformation("submit_plan: fast-path auto-approval for {TaskId}", _baseContext.TaskId);
            return "PLAN AUTO-APPROVED (mechanical round: the rework context already prescribes the exact steps) — proceed.";
        }

        _logger?.LogInformation("submit_plan: evaluating plan for {TaskId} (revision {Rev})", _baseContext.TaskId, _state.Revisions);
        var ctx = _baseContext with { Plan = plan, Ct = ct };
        var verdict = await _pipeline.EvaluateAsync(
            RunGatePipeline.PreImplementationCheckpoint, ctx, _state);
        _state.PlanText = plan;

        if (verdict.Outcome == GateOutcome.Approve)
        {
            _state.PlanApproved = true;
            _logger?.LogInformation("submit_plan: plan APPROVED for {TaskId}", _baseContext.TaskId);
            return "PLAN APPROVED — proceed with implementation. Mutating commands are now unlocked.";
        }

        _state.Revisions++;
        if (_state.Revisions > RunGateState.MaxRevisions)
        {
            _state.PlanFailed = true;
            _logger?.LogWarning("submit_plan: plan REJECTED (final) for {TaskId}", _baseContext.TaskId);
            return $"PLAN REJECTED (final — revision budget exhausted):\n{verdict.Feedback}\n\nDo NOT attempt further edits. Report the rejection and stop.";
        }
        _logger?.LogInformation("submit_plan: plan REVISE ({Rev}/{Max}) for {TaskId}", _state.Revisions, RunGateState.MaxRevisions, _baseContext.TaskId);
        return $"PLAN NEEDS REVISION (revision {_state.Revisions} of {RunGateState.MaxRevisions}):\n{verdict.Feedback}\n\nRevise the plan and resubmit via submit_plan.";
    }

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        (string plan) => SubmitPlan(plan, CancellationToken.None),
        name: "submit_plan",
        description: "Submit your structured implementation plan for approval. REQUIRED before any file edit or git mutation. Sections needed: goal, files, approach, test, done.");
}
