using Microsoft.Extensions.Logging;

namespace Forge.Core;

/// <summary>Lifecycle events observed by the orchestrator (GitHub
/// state, run registry, dispatch outcomes). The machine is
/// event-sourced from OBSERVED state, never intended state — every
/// corruption incident came from trusting intent over observation.</summary>
public enum TaskEvent
{
    Dispatched,
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
    private readonly IIssueStore _issues;
    private readonly bool _writeAuthority;
    private readonly ILogger _logger;

    public TaskStateMachine(
        IIssueStore issues,
        bool writeAuthority,
        ILogger logger)
    {
        _issues = issues;
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
            TaskLifecycleState.Pending, TaskLifecycleState.ReworkQueued);
        Add(TaskEvent.RunCompletedDiff, TaskLifecycleState.Dispatching,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkRunning, TaskLifecycleState.Dispatching);
        Add(TaskEvent.RunCompletedNoDiff, TaskLifecycleState.StalledRework,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkRunning);
        Add(TaskEvent.RunDied, TaskLifecycleState.StalledRework,
            TaskLifecycleState.AgentRunning, TaskLifecycleState.ReworkRunning, TaskLifecycleState.Dispatching);
        Add(TaskEvent.PrOpened, TaskLifecycleState.PROpen, TaskLifecycleState.Dispatching, TaskLifecycleState.PROpen);
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
            TaskLifecycleState.Failed, TaskLifecycleState.BlockedOperator);
        Add(TaskEvent.OperatorBlocked, TaskLifecycleState.BlockedOperator, working);
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
    /// </summary>
    public async Task<TaskLifecycleState> ReportAsync(
        IssueRecord task,
        TaskEvent evt,
        IssueRecord? watch,
        bool hasActiveDevRun,
        CancellationToken ct,
        IReadOnlyDictionary<string, object>? extraMetadata = null)
    {
        var now = DateTime.UtcNow;
        var current = TaskStateProjector.Derive(task, watch, hasActiveDevRun, now).State;
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
        await _issues.TransitionAsync(task.Id, task.Status, error: null, metadata: metadata, ct: ct);
        return next;
    }
}
