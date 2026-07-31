using Forge.Core;

namespace Forge.Dashboard.Now;

/// <summary>
/// The operator-facing "Now" feed: derives attention items, live
/// activity, and plain-language waiting reasons from existing
/// stores. Pure functions over already-loaded records so the
/// derivation is unit-testable; the endpoint just loads + shapes.
///
/// <para>Design rule: answer the operator's three questions —
/// what needs me, what's happening, why is it waiting — without
/// making them read raw status/metadata. Still zero new writes.</para>
/// </summary>
public static class NowFeed
{
    public sealed record AttentionItem(
        string Severity,   // "fail" | "warn" | "info"
        string Kind,
        string Title,
        string? Detail,
        string? IssueId);

    public sealed record LiveItem(string IssueId, string Title, string Stage, long ElapsedMs, string? Phase = null);
    public sealed record WaitingItem(string IssueId, string Title, string Reason, long WaitingMs);

    /// <summary>Failed/Blocked tasks + breaker risks + unverified no-ops.</summary>
    public static IReadOnlyList<AttentionItem> BuildAttention(
        IReadOnlyList<IssueRecord> issues,
        IReadOnlyDictionary<string, bool> gates,
        DateTime utcNow)
    {
        var items = new List<AttentionItem>();
        foreach (var i in issues)
        {
            if (AgentTaskTypes.IsContainer(i.Type) || i.Type == AgentTaskTypes.PrWatch) continue;

            if (i.Status == IssueStatus.Failed)
            {
                items.Add(new("fail", "failed-task",
                    $"{i.Id} failed: {i.Title}",
                    i.GetMetadata("lastError") ?? "see task events", i.Id));
            }
            else if (i.Status == IssueStatus.Blocked)
            {
                items.Add(new("fail", "blocked-task",
                    $"{i.Id} blocked: {i.Title}",
                    i.GetMetadata("reworkAttempts") is { } r ? $"rework circuit breaker at {r}/3" : "blocked",
                    i.Id));
            }
            else if (i.Status == IssueStatus.Pending)
            {
                if (int.TryParse(i.GetMetadata("noProgressAttempts"), out var np) && np > 0)
                {
                    items.Add(new(np >= 2 ? "fail" : "warn", "breaker-risk",
                        $"{i.Id}: {np}/3 no-progress attempts used",
                        "agent runs produced no diff — next attempt " + (np >= 2 ? "may trip the breaker" : "is queued"),
                        i.Id));
                }
                if (int.TryParse(i.GetMetadata("reworkAttempts"), out var rw) && rw > 0)
                {
                    items.Add(new(rw >= 2 ? "fail" : "warn", "breaker-risk",
                        $"{i.Id}: rework round {rw}/3 in flight",
                        i.GetMetadata("reworkContext"), i.Id));
                }
                if (IsAwaitingReview(i, utcNow))
                {
                    items.Add(new("info", "awaiting-review",
                        $"{i.Id}: PR #{i.GetMetadata("prNumber")} awaiting review",
                        "no verdict against the current head — the reviewer may be down or the review trigger missed",
                        i.Id));
                }
            }
            else if (i.Status == IssueStatus.InProgress && IsAwaitingReview(i, utcNow))
            {
                items.Add(new("info", "awaiting-review",
                    $"{i.Id}: PR #{i.GetMetadata("prNumber")} awaiting review",
                    "no verdict against the current head — the reviewer may be down or the review trigger missed",
                    i.Id));
            }
            else if (i.Status == IssueStatus.Completed
                && i.GetMetadata("prNumber") is null
                && i.UpdatedAt >= utcNow.AddHours(-24))
            {
                // The no-op verdict hole: completed without a PR, so
                // nothing was reviewed. Worth an operator glance.
                items.Add(new("info", "unverified-noop",
                    $"{i.Id} self-verified no changes needed",
                    "completed without a PR — unreviewed", i.Id));
            }
        }

        foreach (var (stage, held) in gates)
        {
            if (held)
            {
                items.Add(new("warn", "held-gate",
                    $"Gate held: {stage}",
                    stage == "merge" ? "PRs will not auto-merge until released"
                        : $"the {stage} stage is paused until released",
                    null));
            }
        }

        // fail first, then warn, then info; newest activity first within.
        return items
            .OrderBy(a => a.Severity == "fail" ? 0 : a.Severity == "warn" ? 1 : 2)
            .ToList();
    }

    /// <summary>In-flight work with a plain-English stage.
    /// <paramref name="phases"/> maps task id → the live run's phase
    /// label (plan gate / implementing / verifying n/3 / reviewing)
    /// so a verifying run doesn't read as idle.</summary>
    public static IReadOnlyList<LiveItem> BuildLive(
        IReadOnlyList<IssueRecord> issues, DateTime utcNow,
        IReadOnlyDictionary<string, string?>? phases = null)
        => issues
            .Where(i => i.Status == IssueStatus.InProgress && i.Type != AgentTaskTypes.PrWatch && !AgentTaskTypes.IsContainer(i.Type))
            .OrderByDescending(i => i.UpdatedAt)
            .Select(i => new LiveItem(i.Id, i.Title, StageOf(i), (long)(utcNow - i.UpdatedAt).TotalMilliseconds,
                phases is not null && phases.TryGetValue(i.Id, out var p) ? p : null))
            .ToList();

    private static string StageOf(IssueRecord i) => i.DispatchCheckpoint switch
    {
        null or DispatchCheckpoint.Claimed => "claimed — preparing workspace",
        DispatchCheckpoint.WorktreeAcquired => i.GetMetadata("prNumber") is not null ? "agent running (rework round)" : "agent running",
        DispatchCheckpoint.CommitDone => "pushing",
        DispatchCheckpoint.PushDone => "opening PR",
        >= DispatchCheckpoint.PrOpened => "in review — CI + reviewer",
        _ => "running",
    };

    /// <summary>The review-wait threshold: the event-driven trigger
    /// starts the reviewer at PR-open, so a PR with no verdict against
    /// its current head this long after open means the reviewer is
    /// down, cooling on a 429, or the trigger missed.</summary>
    private static readonly TimeSpan AwaitingReviewAfter = TimeSpan.FromMinutes(10);

    /// <summary>True when the task has an open PR with no verdict
    /// against the current pushed head (branchSha), no review
    /// actively running (reviewStartedAt), and the PR has been open
    /// past the review-wait threshold (anchored to prOpenedAt).</summary>
    private static bool IsAwaitingReview(IssueRecord i, DateTime utcNow)
    {
        if (i.GetMetadata("prNumber") is null) return false;
        if (i.GetMetadata("reviewStartedAt") is not null) return false;
        var verdict = i.GetMetadata("reviewVerdict");
        var reviewedSha = i.GetMetadata("reviewSha");
        var headSha = i.GetMetadata("branchSha");
        var verdictCurrent = !string.IsNullOrEmpty(verdict)
            && (headSha is null || string.Equals(reviewedSha, headSha, StringComparison.Ordinal));
        if (verdictCurrent) return false;
        var anchor = DateTimeOffset.TryParse(i.GetMetadata("prOpenedAt"), out var openedAt)
            ? openedAt.UtcDateTime : i.UpdatedAt;
        return utcNow - anchor > AwaitingReviewAfter;
    }

    /// <summary>One-line reason each Pending item is waiting.</summary>
    public static WaitingItem Reason(
        IssueRecord i,
        bool inActiveSprint,
        bool hasSpecChain,
        string? activeSprintName,
        string? lastTransitionDetail,
        DateTime utcNow)
    {
        string reason;
        if (i.GetMetadata("prNumber") is not null)
        {
            reason = $"rework round {(int.TryParse(i.GetMetadata("reworkAttempts"), out var r) ? r + 1 : 1)} queued — PR stays open";
        }
        else if (lastTransitionDetail?.Contains("llm-429", StringComparison.Ordinal) == true)
        {
            reason = "retrying after LLM rate limit (provider 429)";
        }
        else if (lastTransitionDetail?.Contains("no diff", StringComparison.Ordinal) == true)
        {
            reason = "retrying — last run produced no changes";
        }
        else if (inActiveSprint)
        {
            reason = $"in sprint '{activeSprintName}' — queued for dispatch";
        }
        else if (!hasSpecChain
            && !string.Equals(i.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
        {
            reason = "awaiting technical grooming";
        }
        else
        {
            reason = "groomed — waiting for next sprint";
        }
        return new WaitingItem(i.Id, i.Title, reason, (long)(utcNow - i.UpdatedAt).TotalMilliseconds);
    }
}
