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
        public string Label { get; init; } = "";
        public string? NextActor { get; init; }
        public bool HumanReviewRequired { get; init; }
        public string? NominatedHuman { get; init; }
    }

    public static Situation Describe(IssueRecord issue, SprintPlan? plan = null, string? nominatedHuman = null)
    {
        var held = bool.TryParse(issue.GetMetadata("humanReviewRequired"), out var required) && required;
        Situation Result(string label, string reason, string? actor = null, string tone = "info") =>
            new(reason, tone) { Label = label, NextActor = actor, HumanReviewRequired = held, NominatedHuman = nominatedHuman };
        if (issue.Status == IssueStatus.Completed)
            return plan is not null && Completion.ExistingImplementationEvidence.TryGet(issue, plan, out _)
                ? Result("Existing implementation verified", "Independent verification and review established the approved behavior at a recorded revision.")
                : HasObservedMerge(issue)
                ? Result("Merged", "Merge observed. Release and acceptance are tracked with the sprint.")
                : Result("Completion recorded", issue.GetMetadata("prNumber") is not null
                    ? "Merge evidence unavailable." : "See the task evidence for the recorded outcome.");
        if (issue.Status == IssueStatus.Closed) return Result("Closed", "Work is no longer active.");
        var state = issue.GetMetadata("state");
        if (issue.Status is IssueStatus.Failed or IssueStatus.Blocked)
        {
            var terminal = DescribeLegacy(issue);
            return terminal with { Label = "Needs attention", NextActor = terminal.Tone == "action" ? "Operator" : "Forge", HumanReviewRequired = held, NominatedHuman = nominatedHuman };
        }
        if (state == "ReworkRunning") return Result("Making a requested change", issue.GetMetadata("reworkReason") ?? "The worker is addressing feedback.", "Worker");
        if (state == "ReworkQueued") return Result("Change queued", issue.GetMetadata("reworkReason") ?? "A worker will address the requested change.", "Worker");
        if (state == "AgentRunning") return Result("Implementing", "The worker is preparing and verifying the change.", "Worker");
        if (bool.TryParse(issue.GetMetadata("awaitingHumanReview"), out var waiting) && waiting)
            return Result("Waiting for human review", "Review the current PR revision before merge.", nominatedHuman is null ? "Human reviewer" : "@" + nominatedHuman, "action");
        if (issue.Status == IssueStatus.Pending)
            return Result("Queued", issue.GetMetadata("reworkReason") ?? "Waiting for an available worker.", "Worker");
        if (state == "MergeReady") return Result("Final merge checks", "Forge is checking review, CI and merge requirements.", "Forge");
        var legacy = DescribeLegacy(issue);
        return legacy with { Label = issue.Status is IssueStatus.Blocked or IssueStatus.Failed ? "Needs attention" : "In progress",
            NextActor = legacy.Tone == "action" ? "Operator" : "Forge", HumanReviewRequired = held, NominatedHuman = nominatedHuman };
    }

    public static bool HasObservedMerge(IssueRecord issue)
    {
        try
        {
            var evidence = System.Text.Json.JsonSerializer.Deserialize<Completion.ObservedMergeEvidence>(issue.GetMetadata("observedMerge") ?? "null");
            return evidence is not null && evidence.TaskId == issue.Id
                && int.TryParse(issue.GetMetadata("prNumber"), out var pr) && evidence.PullRequestNumber == pr
                && System.Text.RegularExpressions.Regex.IsMatch(evidence.MergeSha ?? "", "^[a-fA-F0-9]{40}$")
                && System.Text.RegularExpressions.Regex.IsMatch(evidence.HeadSha ?? "", "^[a-fA-F0-9]{40}$");
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static Situation DescribeLegacy(IssueRecord issue)
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
                    return new Situation($"Automatic {why} stopped at the configured retry limit. Review the history before retrying.", "action");
                }
                if (err.Contains("conflict", StringComparison.OrdinalIgnoreCase))
                    return new Situation("The PR conflicts with its base branch. Review the conflict before retrying.", "action");
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
                        // Either ReviewApproved or CiGreen can enter this state; the watcher still checks both and the merge gate.
                        return new Situation("awaiting final merge checks", "info");
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
            return "push hygiene: junk/oversized files — clean up, review and retry";
        if (err.Contains("pre-push verification", StringComparison.OrdinalIgnoreCase))
            return "pre-push build/tests failed — review and retry for another fix round";
        if (err.Contains("429", StringComparison.OrdinalIgnoreCase)
            || err.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || err.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return "LLM rate-limited mid-run (transient) — review and retry";
        if (err.Contains("tool_calls", StringComparison.OrdinalIgnoreCase))
            return "provider protocol error (transient) — review and retry";
        if (err.Contains("PK__issue", StringComparison.OrdinalIgnoreCase)
            || err.Contains("SqlException", StringComparison.OrdinalIgnoreCase))
            return "store fault — review and retry";
        if (err.Contains("pr-stale", StringComparison.OrdinalIgnoreCase))
            return "watch went stale — review and retry restarts the window";
        if (err.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase)
            || err.Contains("max rework", StringComparison.OrdinalIgnoreCase))
            return "Automatic recovery reached its retry limit. Review the history before retrying.";
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
