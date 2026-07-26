namespace Forge.Core;

/// <summary>The explicit lifecycle state of a task (read model,
/// Phase 1: derived from existing task/watch/run data — nothing here
/// changes behavior). Phase 2 makes these authoritative via
/// TaskStateMachine.Transition; Phase 3 deletes the flag guards the
/// derivation currently reads.</summary>
public enum TaskLifecycleState
{
    Pending,
    Dispatching,
    AgentRunning,
    ReworkQueued,
    ReworkRunning,
    StalledRework,
    PROpen,
    ParkedInfra,
    MergeReady,
    Merged,
    Completed,
    BlockedOperator,
    Failed,
    Closed,
}

/// <summary>The derived state plus the operator-facing "what is it
/// waiting on" and strike budget.</summary>
public sealed record TaskStateInfo(
    TaskLifecycleState State,
    // Substate detail: "starting" | "planning" | "implementing" for
    // AgentRunning/ReworkRunning (from the plan-gate record), else null.
    string? Substate,
    string WaitingOn,
    int Strikes,
    int MaxStrikes);

/// <summary>
/// Derives the lifecycle state from the task row, its PR-watch row
/// (if any), and whether a dev run is currently active. Pure
/// function — no I/O, no GitHub calls (CI/mergeable live only in
/// the watcher transiently; states that need them are approximated
/// from the persisted verdict/markers).
/// </summary>
public static class TaskStateProjector
{
    /// <summary>Same window as PRWatcher's rework-round grace: a
    /// claimed round untouched longer than this is stalled.</summary>
    public static readonly TimeSpan StallGrace = TimeSpan.FromMinutes(35);

    public const int MaxStrikes = 3;

    public static TaskStateInfo Derive(
        IssueRecord task,
        IssueRecord? watch,
        bool hasActiveDevRun,
        DateTime utcNow)
    {
        var prNumber = task.GetMetadata("prNumber");
        var reworkReason = task.GetMetadata("reworkReason");
        var strikes = int.TryParse(task.GetMetadata("reworkAttempts"), out var s) ? s : 0;
        var inFlightSha = watch?.GetMetadata("reworkInFlightSha");
        // Phase 3: the machine's record on the task is primary for
        // the park state; the legacy watch flag is the fallback.
        var machineState = task.GetMetadata("state");
        var parked = string.Equals(machineState, nameof(TaskLifecycleState.ParkedInfra), StringComparison.Ordinal)
            ? task.GetMetadata("parkedForSha") ?? "parked"
            : watch?.GetMetadata("parkedOnMainCiSha");

        // Terminal states first.
        switch (task.Status)
        {
            case IssueStatus.Completed:
                return new(prNumber is not null ? TaskLifecycleState.Merged : TaskLifecycleState.Completed,
                    null, "done", strikes, MaxStrikes);
            case IssueStatus.Failed:
                return new(TaskLifecycleState.Failed, null, "operator (failed — inspect, then requeue or close)", strikes, MaxStrikes);
            case IssueStatus.Blocked:
                return new(TaskLifecycleState.BlockedOperator, null, "operator decision required", strikes, MaxStrikes);
            case IssueStatus.Closed:
                return new(TaskLifecycleState.Closed, null, "closed", strikes, MaxStrikes);
        }

        // Parked on infra: the watch recorded that the PR's CI
        // failure is pre-existing on the base branch.
        if (parked is not null)
        {
            return new(TaskLifecycleState.ParkedInfra, null,
                "base-branch CI recovery (parked — no strikes burning)", strikes, MaxStrikes);
        }

        // A live dev run trumps everything else. Substate from the
        // plan-gate record: no record yet = starting; record with no
        // approval = planning; approved = implementing.
        if (hasActiveDevRun)
        {
            var substate = SubstateOf(task);
            return new(strikes > 0 || inFlightSha is not null ? TaskLifecycleState.ReworkRunning : TaskLifecycleState.AgentRunning,
                substate, $"dev agent ({substate})", strikes, MaxStrikes);
        }

        // Rework bookkeeping without a live run.
        if (inFlightSha is not null || (prNumber is not null && reworkReason is not null))
        {
            if (task.Status == IssueStatus.Pending)
            {
                return new(TaskLifecycleState.ReworkQueued, null,
                    "dispatch slot (rework round queued)", strikes, MaxStrikes);
            }
            // Claimed but no live run: fresh = starting, stale = stalled.
            if (utcNow - task.UpdatedAt > StallGrace)
            {
                return new(TaskLifecycleState.StalledRework, null,
                    $"stalled — no push and no task update for {(int)(utcNow - task.UpdatedAt).TotalMinutes}m (stall-breaker re-fires as a strike)",
                    strikes, MaxStrikes);
            }
            return new(TaskLifecycleState.ReworkRunning, "starting",
                "dev agent (starting)", strikes, MaxStrikes);
        }

        if (task.Status == IssueStatus.Pending)
        {
            return new(TaskLifecycleState.Pending, null,
                "dispatch slot (first run)", strikes, MaxStrikes);
        }

        // InProgress with a PR: waiting on the review/merge side.
        if (prNumber is not null)
        {
            var verdict = watch?.GetMetadata("reviewVerdict");
            var verdictSha = watch?.GetMetadata("reviewSha");
            // The verdict only counts at the head it was issued for;
            // the projector can't see the live head, so an Approve
            // verdict is reported as "approved at last known head".
            if (string.Equals(verdict, "Approve", StringComparison.Ordinal) && verdictSha is not null)
            {
                return new(TaskLifecycleState.MergeReady, null,
                    $"merge gate (approved at {verdictSha[..Math.Min(7, verdictSha.Length)]}, CI permitting)", strikes, MaxStrikes);
            }
            return new(TaskLifecycleState.PROpen, null,
                "CI + reviewer verdict", strikes, MaxStrikes);
        }

        // InProgress, no PR, no live run: the workflow is between
        // executors (or recovering).
        return new(TaskLifecycleState.Dispatching, null,
            "workflow (between steps)", strikes, MaxStrikes);
    }

    private static string SubstateOf(IssueRecord task)
    {
        var raw = task.GetMetadata("planGate");
        if (raw is null) return "starting";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var approved = doc.RootElement.TryGetProperty("approved", out var ap) && ap.GetBoolean();
            return approved ? "implementing" : "planning";
        }
        catch { return "starting"; }
    }
}
