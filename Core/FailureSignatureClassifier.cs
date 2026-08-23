namespace Forge.Core;

/// <summary>
/// Deterministic failure-signature classifier (failure-triage phase 1).
/// Pure function: maps a failed/blocked task's error text + metadata to
/// a normalized (signature, classification) pair so the ledger can
/// group repeat failures. No LLM — the taxonomy is the empirical
/// failure table from the 2026-08-23 exploration. Unknown failures land
/// in <see cref="OtherSignature"/>; <c>code-bug-suspect</c> is DERIVED
/// (same signature across ≥3 distinct tasks) at read time, never stored.
/// </summary>
public static class FailureSignatureClassifier
{
    public const string OtherSignature = "other";
    public const string Unclassified = "unclassified";

    /// <summary>Match order is significant: the most specific root-cause
    /// patterns run first; the breaker-trip rule is the catch-all for
    /// budget exhaustion, and anything unrecognized is 'other'.</summary>
    public static (string Signature, string Classification) Classify(
        string? error, IReadOnlyDictionary<string, string>? metadata)
    {
        var text = error ?? "";
        var planGate = Meta(metadata, "planGate") ?? "";
        var haystack = text + "\n" + planGate;
        var lastEvent = Meta(metadata, "lastEvent") ?? "";

        // state-poison: the persisted session/worktree is the problem.
        if (Contains(haystack, "tool_calls") && Contains(haystack, "must be followed by tool messages")
            || Contains(haystack, "tool call result does not follow tool call")
            || Contains(haystack, "tool_use") && Contains(haystack, "tool_result")
                && (Contains(haystack, "without") || Contains(haystack, "unexpected"))
            || Contains(haystack, "exceeded model token limit")
            || Contains(haystack, "total message size") && Contains(haystack, "exceeds")
            || Contains(haystack, "context window exceeds limit"))
            return ("session-pairing-400", "state-poison");

        if (Contains(haystack, "diverged from PR head")
            || Contains(haystack, "non-fast-forward")
            || Contains(haystack, "fossil"))
            return ("rework-fossil", "state-poison");

        if (Contains(haystack, "already merged")
            || Contains(haystack, "tree-identical")
            || Contains(haystack, "tarpit"))
            return ("merged-tarpit", "state-poison");

        // transient-upstream: provider/gateway outages.
        if (Contains(haystack, "529") || Contains(haystack, "overloaded"))
            return ("llm-529-overload", "transient-upstream");
        if (Contains(haystack, "429") || Contains(haystack, "rate limit") || Contains(haystack, "quota"))
            return ("llm-429-quota", "transient-upstream");
        if (Contains(haystack, "bad gateway")
            || Contains(haystack, "502") || Contains(haystack, "503") || Contains(haystack, "504")
            || Contains(haystack, "service unavailable"))
            return ("gateway-5xx", "transient-upstream");

        // no-progress: the run completed but produced nothing.
        if (Contains(haystack, "no diff") || Contains(haystack, "empty diff")
            || string.Equals(lastEvent, "RunCompletedNoDiff", StringComparison.Ordinal)
            || int.TryParse(Meta(metadata, "noProgressAttempts"), out var noProgress) && noProgress >= 3)
            return ("no-diff-bounce", "no-progress");

        // verification: pre-push build/test gate.
        if (Contains(haystack, "verification") && Contains(haystack, "timed out"))
            return ("verification-timeout", "verification");
        if (Contains(haystack, "verification failed") || Contains(haystack, "verification fail"))
            return ("verification-fail", "verification");

        // gate-loop: the plan gate kept rejecting.
        if (Contains(haystack, "plan-territory")
            || Contains(haystack, "territory") && Contains(haystack, "plan"))
            return ("plan-gate-territory", "gate-loop");
        if (Contains(haystack, "plan gate") || Contains(haystack, "planGate")
            || Contains(haystack, "plan-schema") || Contains(haystack, "plan-llm-review"))
            return ("plan-gate-revisions", "gate-loop");

        // review-loop: rework rounds on requested changes.
        if (Contains(haystack, "changes requested") || Contains(haystack, "RequestChanges"))
            return ("review-changes-loop", "review-loop");

        // capability-bound: the strike budget ran out.
        if (string.Equals(lastEvent, "BreakerTripped", StringComparison.Ordinal)
            || Contains(haystack, "circuit breaker") || Contains(haystack, "breaker")
            || Strikes(metadata) >= 3)
            return ("breaker-exhausted", "capability-bound");

        return (OtherSignature, Unclassified);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? Meta(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var v) ? v : null;

    private static int Strikes(IReadOnlyDictionary<string, string>? metadata) =>
        int.TryParse(Meta(metadata, "reworkAttempts"), out var n) ? n : 0;
}
