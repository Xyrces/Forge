using Microsoft.Extensions.Logging;

namespace Forge.Core;

/// <summary>Lifecycle events observed by the orchestrator (GitHub
/// state, run registry, dispatch outcomes). The machine is
/// event-sourced from OBSERVED state, never intended state — every
/// corruption incident came from trusting intent over observation.</summary>
public enum TaskEvent
{
    Dispatched,
    RunStarted,
    RunCompletedDiff,
    RunCompletedNoDiff,
    RunDied,
    PrOpened,
    ReworkFired,
    StallDetected,
    ParkedOnInfra,
    BaseRecovered,
    HeadMoved,
    CiGreen,
    CiRedOnPr,
    ReviewApproved,
    ReviewChangesRequested,
    ConflictDetected,
    BreakerTripped,
    Merged,
    ExternallyMerged,
    OperatorRequeue,
    OperatorBlocked,
    OperatorClosed,
    WatchResumed,
}

/// <summary>
/// Phase 2 write-path: the legal-transition table for the task
/// lifecycle. Writers (PRWatcher, ReviewerDispatcher,
/// OrchestratorAgent, SprintAssembler, StartupRecovery) report
/// events here; the machine validates (derived current state,
/// event) against the table, records <c>state</c> /
/// <c>stateEnteredAt</c> metadata, and returns the new state.
///
/// SHADOW MODE (config state.writeAuthority=false, the default
/// during migration): illegal transitions are logged as warnings
/// but allowed — validation data without risk. Authority mode
/// (true): illegal transitions are logged as errors (never thrown
/// in production paths — the MAF swallowed-fault lesson — but the
/// <c>stateViolation</c> metadata makes them visible).
/// </summary>
public sealed class TaskStateMachine
{
    private readonly bool _writeAuthority;
    private readonly ILogger _logger;

    /// <summary>The machine is store-AGNOSTIC (multi-project fix
    /// 2026-07-29): every report passes the store that owns the task.
    /// The previous construction-time store silently wrote lifecycle
    /// state to the primary project's store, so a second project's
    /// tasks failed every report with "Issue not found" and their
    /// state never advanced.</summary>
    public TaskStateMachine(
        bool writeAuthority,
        ILogger logger)
    {
        _writeAuthority = writeAuthority;
        _logger = logger;
    }

    /// <summary>(event, from-state) -> to-state. A transition absent
    /// from the table is ILLEGAL. Self-transitions (from == to) are
    /// always legal (idempotent re-observation).</summary>
    private static readonly IReadOnlyDictionary<(TaskEvent, TaskLifecycleState), TaskLifecycleState> Table =
        BuildTable();

    private static IReadOnlyDictionary<(TaskEvent, TaskLifecycleState), TaskLifecycleState> BuildTable()
    {
        var t = new Dictionary<(TaskEvent, TaskLifecycleState), TaskLifecycleState>();
        void Add(TaskEvent e, TaskLifecycleState to, params TaskLifecycleState[] from)
        {
            foreach (var f in from) t[(e, f)] = to;
        }
        var working = new[]
        {
            TaskLifecycleState.Pending, TaskLifecycleState.Dispatching, TaskLifecycleState.AgentRunning,
            TaskLifecycleState.ReworkQueued, TaskLifecycleState.ReworkRunning, TaskLifecycleState.StalledRework,
            TaskLifecycleState.PROpen, TaskLifecycleState.ParkedInfra, TaskLifecycleState.MergeReady,
        };

        Add(TaskEvent.Dispatched, TaskLifecycleState.Dispatching,
            TaskLifecycleState.Pending, TaskLifecycleState.ReworkQueued,
            // Re-dispatch after a died/timed-out run (the task
            // requeues and is claimed again — observed live
            // 2026-07-26: Dispatching+Dispatched and
            // StalledRework+Dispatched violations).
            TaskLifecycleState.Dispatching, TaskLifecycleState.StalledRework,
            // Claim-race tolerance: a CI/review requeue makes the task
            // claimable while the record still says PROpen (observed
            // live 2026-07-30: task-9 violation during the rework
            // storm). Same class as the ReworkQueued entries.
            TaskLifecycleState.PROpen);
        Add(TaskEvent.RunStarted, TaskLifecycleState.AgentRunning,
            // The model run actually begins — advances the recorded
            // state and (critically) refreshes stateEnteredAt, so the
            // stall guard's clock measures from run-start, not from
            // the rework fire (observed live 2026-07-27: retried
            // stalls looked frozen at Dispatching for the whole run).
            TaskLifecycleState.Dispatching, TaskLifecycleState.AgentRunning,
            // Claim-race tolerance: the requeue transition makes the
            // task claimable before the ReworkFired machine write is
            // visible, so the run's events can arrive while the record
            // still says ReworkQueued (observed live 2026-07-29:
            // task-12's record stranded after a Dispatched violation).
            TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.RunCompletedDiff, TaskLifecycleState.PROpen,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkRunning,
            TaskLifecycleState.Dispatching, TaskLifecycleState.PROpen,
            TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.RunCompletedNoDiff, TaskLifecycleState.Completed,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkRunning, TaskLifecycleState.Completed,
            // A fast no-diff run can complete before the RunStarted
            // report lands (sibling events RunCompletedDiff and
            // RunDied already allow Dispatching — observed live
            // 2026-07-29: porthorizon task-11 stateViolation).
            TaskLifecycleState.Dispatching, TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.RunDied, TaskLifecycleState.StalledRework,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkRunning,
            TaskLifecycleState.Dispatching, TaskLifecycleState.PROpen,
            TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.PrOpened, TaskLifecycleState.PROpen,
            TaskLifecycleState.Dispatching, TaskLifecycleState.PROpen,
            // A rework round's workflow re-uses the open PR — the
            // dispatch-completed report observes PrOpened while the
            // round record is live (observed live 2026-07-26).
            TaskLifecycleState.ReworkRunning,
            // A push after approval: the new head invalidates the
            // approval — back to PROpen (observed live: task-193,
            // MergeReady+PrOpened).
            TaskLifecycleState.MergeReady,
            TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.ReworkFired, TaskLifecycleState.ReworkQueued,
            TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady, TaskLifecycleState.StalledRework,
            TaskLifecycleState.ParkedInfra, TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.StallDetected, TaskLifecycleState.StalledRework,
            TaskLifecycleState.ReworkRunning, TaskLifecycleState.ReworkQueued, TaskLifecycleState.StalledRework);
        Add(TaskEvent.ParkedOnInfra, TaskLifecycleState.ParkedInfra, TaskLifecycleState.PROpen, TaskLifecycleState.ParkedInfra);
        Add(TaskEvent.BaseRecovered, TaskLifecycleState.ReworkQueued, TaskLifecycleState.ParkedInfra);
        Add(TaskEvent.HeadMoved, TaskLifecycleState.PROpen,
            TaskLifecycleState.ReworkRunning, TaskLifecycleState.PROpen, TaskLifecycleState.StalledRework);
        Add(TaskEvent.CiGreen, TaskLifecycleState.MergeReady, TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady);
        Add(TaskEvent.CiRedOnPr, TaskLifecycleState.ReworkQueued,
            TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady, TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.ReviewApproved, TaskLifecycleState.MergeReady, TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady);
        Add(TaskEvent.ReviewChangesRequested, TaskLifecycleState.ReworkQueued, TaskLifecycleState.PROpen, TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.ConflictDetected, TaskLifecycleState.ReworkQueued,
            TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady, TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.BreakerTripped, TaskLifecycleState.Failed, working);
        Add(TaskEvent.Merged, TaskLifecycleState.Merged,
            TaskLifecycleState.MergeReady, TaskLifecycleState.PROpen);
        Add(TaskEvent.ExternallyMerged, TaskLifecycleState.Merged, working);
        Add(TaskEvent.OperatorRequeue, TaskLifecycleState.Pending,
            TaskLifecycleState.Failed, TaskLifecycleState.BlockedOperator,
            // Operator requeues land on DB-Failed/Blocked tasks, but
            // the machine record under a DB-Blocked row can be any
            // PR-phase or stalled state (breaker trips park in
            // StalledRework; infra parks in ParkedInfra; a blocked
            // watch still reads PROpen/MergeReady). The strike-reset
            // endpoint fires OperatorRequeue for all of them
            // (observed live 2026-08-01: task-365 violation from
            // StalledRework on strike-reset).
            TaskLifecycleState.StalledRework, TaskLifecycleState.ParkedInfra,
            TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady);
        Add(TaskEvent.OperatorBlocked, TaskLifecycleState.BlockedOperator, working);
        // Operator close-obsolete (2026-08-01): the operator retires
        // a task outright (work already on main via another task,
        // superseded, won't-fix) from ANY live or failed state. The
        // /api/tasks/{id}/close endpoint reports it; terminal, so no
        // outgoing transitions.
        Add(TaskEvent.OperatorClosed, TaskLifecycleState.Closed,
            TaskLifecycleState.Pending, TaskLifecycleState.Dispatching,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkQueued,
            TaskLifecycleState.ReworkRunning, TaskLifecycleState.StalledRework,
            TaskLifecycleState.PROpen, TaskLifecycleState.ParkedInfra,
            TaskLifecycleState.MergeReady, TaskLifecycleState.Failed,
            TaskLifecycleState.BlockedOperator);
        // Stalled tasks are still polled by the sweep (InProgress +
        // prNumber), so CI/conflict observations keep arriving while
        // the round is parked. They change nothing — the stall guard
        // owns the exit — record them as self-transitions instead of
        // violations (observed live 2026-08-01: task-364 CiGreen and
        // task-370 ConflictDetected violations from StalledRework).
        Add(TaskEvent.CiGreen, TaskLifecycleState.StalledRework, TaskLifecycleState.StalledRework);
        Add(TaskEvent.ConflictDetected, TaskLifecycleState.StalledRework, TaskLifecycleState.StalledRework);
        // Auto-resume of a transiently-blocked watch (e.g. reviewer
        // model rate-limited at block time, available again now): the
        // task re-enters PROpen and the sweep re-reviews the head.
        // PROpen self-transition: the block was written via a raw
        // status transition, so the machine record still reads PROpen
        // when the resume fires (observed live 2026-07-30: task-18/19
        // stateViolation on the first auto-resume).
        Add(TaskEvent.WatchResumed, TaskLifecycleState.PROpen,
            TaskLifecycleState.BlockedOperator, TaskLifecycleState.PROpen);
        return t;
    }

    /// <summary>True when (event, from) -> to is in the table.
    /// Exposed for tests.</summary>
    public static bool IsLegal(TaskEvent evt, TaskLifecycleState from, TaskLifecycleState to) =>
        Table.TryGetValue((evt, from), out var mapped) && mapped == to;

    /// <summary>
    /// Report an observed event for a task: derive the current state,
    /// validate the transition, record state metadata. The legacy
    /// flag writes stay at the call sites until Phase 3 — the
    /// machine SHADOWS them for now. Returns the new state.
    /// <paramref name="issues"/> is the store that OWNS the task
    /// (the task's project store) — the machine writes nowhere else.
    /// </summary>
    public async Task<TaskLifecycleState> ReportAsync(
        IIssueStore issues,
        IssueRecord task,
        TaskEvent evt,
        IssueRecord? watch,
        bool hasActiveDevRun,
        CancellationToken ct,
        IReadOnlyDictionary<string, object>? extraMetadata = null)
    {
        var now = DateTime.UtcNow;
        // Authority semantics (Phase 3): the machine's own recorded
        // state is the current state when present — flag-derivation
        // is only the bootstrap for entities that predate the
        // machine. Deriving from flags here is actively wrong: stale
        // flags (e.g. reworkReason persists after the round pushes)
        // make reality and derivation diverge — observed live as
        // ReworkRunning+ReviewApproved violations (2026-07-26).
        var recorded = task.GetMetadata("state");
        var current = Enum.TryParse<TaskLifecycleState>(recorded, out var parsed)
            ? parsed
            : TaskStateProjector.Derive(task, watch, hasActiveDevRun, now).State;
        var legal = Table.TryGetValue((evt, current), out var next);
        if (!legal)
        {
            var msg = $"ILLEGAL lifecycle transition: {task.Id} is {current}, event {evt} has no table entry";
            if (_writeAuthority)
            {
                _logger.LogError("{Msg} (authority mode)", msg);
            }
            else
            {
                _logger.LogWarning("{Msg} (shadow mode — allowed)", msg);
            }
            // The event is recorded with the violation flag so the
            // operator can audit what reality did vs what the table
            // expected.
            next = current;
        }

        var metadata = new Dictionary<string, object>
        {
            ["state"] = next.ToString(),
            ["stateEnteredAt"] = now.ToString("O"),
            ["lastEvent"] = evt.ToString(),
        };
        if (!legal) metadata["stateViolation"] = $"{current}+{evt}";
        if (extraMetadata is not null)
        {
            foreach (var kv in extraMetadata) metadata[kv.Key] = kv.Value;
        }
        await issues.TransitionAsync(task.Id, task.Status, error: null, metadata: metadata, ct: ct);
        return next;
    }
}
