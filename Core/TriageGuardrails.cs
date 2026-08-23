namespace Forge.Core;

/// <summary>
/// Deterministic triage guardrails (triage phase 2, plan §4) — pure
/// functions over the task's ledger history. Store-enforced, never LLM
/// judgment: both the FailureTriageConsumer (publish/no-publish) and the
/// TriageConsumer (pre-run re-check; hints are not truth) evaluate these
/// before the triage agent is allowed to act.
/// </summary>
public static class TriageGuardrails
{
    /// <summary>Max triage actions per task per rolling 24h.</summary>
    public const int MaxActionsPerTaskPerDay = 2;

    /// <summary>Same signature + same triage action this many times
    /// without a success = the requeue-burn loop; park.</summary>
    public const int MaxSameActionWithoutSuccess = 2;

    public enum Decision
    {
        /// <summary>Under all caps — the agent may run.</summary>
        Allowed,
        /// <summary>Daily action cap reached — auto-park, no LLM.</summary>
        DailyCapReached,
        /// <summary>Same signature + same action repeatedly failed to
        /// succeed — auto-park, no LLM (requeue-burn loop prevention).</summary>
        SameActionBurnLoop,
    }

    /// <summary>Evaluate the task's full ledger history (newest first,
    /// any order works) against the current failure's signature.</summary>
    public static Decision Evaluate(
        IReadOnlyList<FailureTriageEntry> taskHistory, string currentSignature, DateTime nowUtc)
    {
        var dayAgo = nowUtc.AddDays(-1);
        var actionsToday = taskHistory.Count(e =>
            e.Actor == FailureTriageActors.Triage && e.ActedAt >= dayAgo);
        if (actionsToday >= MaxActionsPerTaskPerDay) return Decision.DailyCapReached;

        var sameActionWithoutSuccess = taskHistory.Count(e =>
            e.Actor == FailureTriageActors.Triage
            && e.Action == FailureTriageActions.TriageRequeue
            && e.Signature == currentSignature
            && e.Outcome != FailureTriageOutcomes.Succeeded);
        if (sameActionWithoutSuccess >= MaxSameActionWithoutSuccess) return Decision.SameActionBurnLoop;

        return Decision.Allowed;
    }

    /// <summary>Human/ledger-readable reason for a deterministic park.</summary>
    public static string ParkReason(this Decision decision) => decision switch
    {
        Decision.DailyCapReached =>
            $"daily triage action cap reached ({MaxActionsPerTaskPerDay}/day) — parked for the operator",
        Decision.SameActionBurnLoop =>
            $"same signature requeued {MaxSameActionWithoutSuccess} times without success — parked for the operator (requeue-burn prevention)",
        _ => "parked for the operator",
    };
}
