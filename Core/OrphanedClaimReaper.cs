namespace Forge.Core;

/// <summary>
/// Orphaned-claim reaper (operator-approved auto-remediation,
/// 2026-07-31): a task InProgress with an assignee but NO active run
/// is an orphaned claim — the run died and nothing requeued it.
/// StartupRecovery handles this on restart; the reaper handles it
/// in-process. Mechanical-only by design, and the shared
/// recovery_attempts budget caps automatic recovery at 3 — beyond
/// that the operator is the path.
///
/// <para>
/// Watch-ownership is STATE-AWARE, not prNumber-blanket: a task with
/// a prNumber is the watch's responsibility only while its lifecycle
/// state is a PR-phase state (PROpen / MergeReady / ParkedInfra). A
/// task in an engineering-owed state (ReworkQueued, ReworkRunning,
/// Dispatching, AgentRunning, StalledRework) belongs to engineering
/// even with a PR open — an orphaned claim there stalls the rework
/// round forever: the watcher already fired its verdict and only
/// polls PR-phase tasks, and a prNumber-blanket reaper exclusion
/// skips it (observed live 2026-07-31: task-360/361/362/364 sat
/// InProgress+ReworkQueued with no run after a service restart).
/// Missing state metadata with a prNumber = conservatively
/// watch-owned (skip).
/// </para>
/// </summary>
public static class OrphanedClaimReaper
{
    public const int MaxReapAttempts = 3;
    private static readonly TimeSpan DefaultOrphanAfter = TimeSpan.FromMinutes(30);

    /// <summary>Requeue orphaned claims. Returns the requeued ids.</summary>
    public static async Task<IReadOnlyList<string>> ReapAsync(
        IIssueStore issues, AgentRunStore runs, DateTime utcNow,
        TimeSpan? orphanAfter = null, CancellationToken ct = default)
    {
        var threshold = orphanAfter ?? DefaultOrphanAfter;
        var activeTaskIds = (await runs.ListActiveAsync(ct))
            .Where(r => r.TaskId is not null)
            .Select(r => r.TaskId!)
            .ToHashSet(StringComparer.Ordinal);

        var reaped = new List<string>();
        foreach (var i in await issues.ListAsync(new IssueFilter { Status = IssueStatus.InProgress }, ct))
        {
            if (AgentTaskTypes.IsContainer(i.Type) || i.Type == AgentTaskTypes.PrWatch) continue;
            if (i.Assignee is null) continue;                     // not claimed — that's starvation, not an orphan
            if (i.GetMetadata("prNumber") is not null && IsWatchOwnedPhase(i.GetMetadata("state"))) continue;
            if (activeTaskIds.Contains(i.Id)) continue;           // genuinely running
            if (utcNow - i.UpdatedAt < threshold) continue;       // give the claim time to start its run
            if (i.RecoveryAttempts >= MaxReapAttempts) continue;  // budget exhausted — the watchdog's starvation finding alerts instead

            await issues.IncrementRecoveryAttemptsAsync(i.Id, ct);
            await issues.TransitionAsync(i.Id, IssueStatus.Pending,
                $"orphaned claim requeued — no active run for {(int)(utcNow - i.UpdatedAt).TotalMinutes}m " +
                $"(reap {i.RecoveryAttempts + 1}/{MaxReapAttempts})", ct: ct);
            reaped.Add(i.Id);
        }
        return reaped;
    }

    /// <summary>
    /// A task with a PR is the watch's responsibility only while its
    /// lifecycle state is a PR-phase state. Engineering-owed states
    /// (rework rounds, dispatch/run phases) are reapable; missing
    /// state is conservatively treated as watch-owned. Shared with
    /// StartupRecovery.Classify, which must make the identical
    /// watch-ownership decision at boot.
    /// </summary>
    internal static bool IsWatchOwnedPhase(string? state) => state is null
        || state is nameof(TaskLifecycleState.PROpen)
            or nameof(TaskLifecycleState.MergeReady)
            or nameof(TaskLifecycleState.ParkedInfra);
}
