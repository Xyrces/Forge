namespace Forge.Agents.Gates;

/// <summary>
/// Mutable per-run gate state, shared between the submit_plan tool
/// (writes) and the mutating tools (read via a predicate). One
/// instance per agent run; not thread-safe by design (MAF invokes
/// tools sequentially inside one RunAsync).
/// </summary>
public sealed class RunGateState
{
    /// <summary>Mechanical rounds (conflict sync, infra retrigger)
    /// skip evaluation: the rework context already prescribes the
    /// exact steps, so submit_plan auto-approves.</summary>
    public bool FastPath { get; set; }

    public bool PlanApproved { get; set; }
    public string? PlanText { get; set; }
    public int Revisions { get; set; }

    /// <summary>Set when the revision budget is exhausted — the
    /// runner fails the run with the gate record attached.</summary>
    public bool PlanFailed { get; set; }

    /// <summary>Audit trail: (gate, outcome, feedback) per
    /// evaluation, newest last. Persisted to task metadata when the
    /// run ends.</summary>
    public List<(string Gate, GateOutcome Outcome, string Feedback)> Verdicts { get; } = new();

    public const int MaxRevisions = 2;
}
