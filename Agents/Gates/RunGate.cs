using Microsoft.Extensions.Logging;

namespace Forge.Agents.Gates;

/// <summary>Outcome of a single quality-gate evaluation.</summary>
public enum GateOutcome
{
    Approve,
    Revise,
    Block,
}

/// <summary>Whether a gate is deterministic (rule-based, zero LLM)
/// or uses an LLM call.</summary>
public enum GateKind
{
    Deterministic,
    Llm,
}

public sealed record RunGateVerdict(GateOutcome Outcome, string Feedback)
{
    public static readonly RunGateVerdict Approved = new(GateOutcome.Approve, "approved");
}

/// <summary>Everything a gate needs to judge one plan submission.</summary>
public sealed record RunGateContext(
    string TaskId,
    string RoleName,
    IReadOnlyList<string> TerritoryPrefixes,
    bool TerritoryAllowsRootFiles,
    string WorktreePath,
    string TaskText,
    string Plan,
    CancellationToken Ct);

/// <summary>
/// One deterministic-or-LLM quality gate. Gates are ordered per
/// checkpoint and short-circuit on the first non-Approve verdict.
/// Designed so future human-approval gates plug in unchanged (an
/// async gate parks the run via dispatch_checkpoint and resolves on
/// operator input — not built yet).
/// </summary>
public interface IRunGate
{
    string Name { get; }
    GateKind Kind { get; }
    string Description { get; }
    Task<RunGateVerdict> EvaluateAsync(RunGateContext ctx);
}
