namespace Forge.Core;

/// <summary>
/// One-line human description of a task's situation for board
/// surfaces: WHY it's in its column and WHAT HAPPENS NEXT (automatic
/// or operator action). Operator directive 2026-07-31: "at a minimum
/// I do not have sufficient information to know the status of an
/// issue from the board itself." Pure function of the issue record —
/// no I/O, fully testable. Tones: <c>action</c> = operator must do
/// something; <c>warn</c> = transient, the system is handling it;
/// <c>info</c> = normal flow.
/// </summary>
public static class TaskSituation
{
    public sealed record Situation(string Text, string Tone)
    {
        public static readonly Situation None = new("", "info");
    }

    public static Situation Describe(IssueRecord issue)
    {
        var state = issue.GetMetadata("state");
        var err = issue.GetMetadata("lastError") ?? "";
        var pr = issue.GetMetadata("prNumber");
        var rework = int.TryParse(issue.GetMetadata("reworkAttempts"), out var rw) ? rw : 0;
        var verdict = issue.GetMetadata("reviewVerdict");

        switch (issue.Status)
        {
            case IssueStatus.Failed:
                return new Situation(ClassifyFailure(err), "action");

            case IssueStatus.Blocked:
                if (issue.GetMetadata("blockedKind") == "reviewer-unavailable")
                    return new Situation("reviewer model down · auto-resumes when it recovers", "warn");
                if (err.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("max rework", StringComparison.OrdinalIgnoreCase))
                {
                    var why = (issue.GetMetadata("reworkReason") ?? "").Contains("conflict", StringComparison.OrdinalIgnoreCase)
                        ? "conflict syncs"
                        : "rework rounds";
                    return new Situation($"circuit breaker · {why} ×3 exhausted — clear strikes to retry", "action");
                }
                if (err.Contains("conflict", StringComparison.OrdinalIgnoreCase))
                    return new Situation("PR conflicts with base · sync round needed — clear strikes to retry", "action");
                return new Situation("blocked · operator review required", "action");

            case IssueStatus.Pending:
                if (pr is not null && rework > 0)
                    return new Situation($"queued · fix round R{rework}/3 ({ShortReason(issue.GetMetadata("reworkReason"))})", "info");
                if (pr is not null)
                    return new Situation($"queued · watch adopts PR #{pr} on next sweep", "info");
                if (issue.GetMetadata("requeuedFromFailedAt") is not null)
                    return new Situation("requeued by operator · awaiting slot", "info");
                return new Situation("queued · awaiting CoreDev slot", "info");

            case IssueStatus.InProgress:
                switch (state)
                {
                    case "StalledRework":
                        return new Situation("round stalled · watcher re-fires on next sweep", "warn");
                    case "Dispatching":
                        return new Situation("dispatching…", "info");
                    case "AgentRunning" or "ReworkRunning":
                        return new Situation("agent running", "info");
                    case "MergeReady":
                        return new Situation("approved + CI green · merging", "info");
                    case "ParkedInfra":
                        return new Situation("parked · base-branch CI red, waits for recovery", "warn");
                    case "PROpen":
                        return verdict switch
                        {
                            "Approve" => new Situation("approved · awaiting CI + merge gate", "info"),
                            "RequestChanges" => new Situation($"changes requested · rework R{Math.Max(rework, 1)}/3 queued", "info"),
                            _ => new Situation("PR open · CI + review pending", "info"),
                        };
                    case "ReworkQueued":
                        return new Situation($"rework R{Math.Max(rework, 1)}/3 queued · awaiting slot", "info");
                    case "BlockedOperator":
                        return new Situation("blocked · operator review required", "action");
                    default:
                        return new Situation("in progress", "info");
                }

            default:
                return Situation.None;
        }
    }

    private static string ClassifyFailure(string err)
    {
        if (err.Contains("pre-push hygiene", StringComparison.OrdinalIgnoreCase))
            return "push hygiene: junk/oversized files — clean up, reset & requeue";
        if (err.Contains("pre-push verification", StringComparison.OrdinalIgnoreCase))
            return "pre-push build/tests failed — reset & requeue for another fix round";
        if (err.Contains("429", StringComparison.OrdinalIgnoreCase)
            || err.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || err.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return "LLM rate-limited mid-run (transient) — reset & requeue";
        if (err.Contains("tool_calls", StringComparison.OrdinalIgnoreCase))
            return "provider protocol error (transient) — reset & requeue";
        if (err.Contains("PK__issue", StringComparison.OrdinalIgnoreCase)
            || err.Contains("SqlException", StringComparison.OrdinalIgnoreCase))
            return "store fault — reset & requeue";
        if (err.Contains("pr-stale", StringComparison.OrdinalIgnoreCase))
            return "watch went stale — reset & requeue restarts the window";
        if (err.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase)
            || err.Contains("max rework", StringComparison.OrdinalIgnoreCase))
            return "circuit breaker — clear strikes to retry";
        var first = err.Split('\n', 2)[0];
        return first.Length == 0 ? "failed" : $"failed: {first[..Math.Min(70, first.Length)]}";
    }

    private static string ShortReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "rework";
        if (reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)) return "conflict";
        if (reason.Contains("CI", StringComparison.OrdinalIgnoreCase)) return "CI red";
        if (reason.Contains("review", StringComparison.OrdinalIgnoreCase)) return "review";
        return reason.Split('\n', 2)[0][..Math.Min(30, reason.Length)];
    }
}
