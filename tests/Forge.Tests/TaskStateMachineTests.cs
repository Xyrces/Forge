using Forge.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Phase 2 write-path: the transition table's legality rules and the
/// shadow-mode reporting behavior (state metadata recorded, illegal
/// transitions flagged but allowed, never thrown).
/// </summary>
public class TaskStateMachineTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;

    public TaskStateMachineTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private async Task<IssueRecord> SeedTaskAsync(
        IssueStatus status = IssueStatus.Pending, Dictionary<string, object>? meta = null)
    {
        var t = await _issues.CreateAsync(new NewIssue(
            Type: "task", Title: "t",
            Metadata: meta ?? new Dictionary<string, object>()));
        if (status != IssueStatus.Pending)
        {
            await _issues.TransitionAsync(t.Id, status, error: null);
        }
        return (await _issues.GetAsync(t.Id))!;
    }

    private TaskStateMachine Machine(bool authority = false)
        => new(_issues, authority, NullLogger.Instance);

    [Theory]
    // Happy paths from the 2026-07-25/26 incident trail.
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.Pending, TaskLifecycleState.Dispatching, true)]
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.ReworkQueued, TaskLifecycleState.Dispatching, true)]
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.Dispatching, TaskLifecycleState.Dispatching, true)]
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.StalledRework, TaskLifecycleState.Dispatching, true)]
    [InlineData(TaskEvent.PrOpened, TaskLifecycleState.Dispatching, TaskLifecycleState.PROpen, true)]
    [InlineData(TaskEvent.PrOpened, TaskLifecycleState.ReworkRunning, TaskLifecycleState.PROpen, true)]
    [InlineData(TaskEvent.PrOpened, TaskLifecycleState.MergeReady, TaskLifecycleState.PROpen, true)]
    [InlineData(TaskEvent.CiRedOnPr, TaskLifecycleState.PROpen, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.ParkedOnInfra, TaskLifecycleState.PROpen, TaskLifecycleState.ParkedInfra, true)]
    [InlineData(TaskEvent.BaseRecovered, TaskLifecycleState.ParkedInfra, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.StallDetected, TaskLifecycleState.ReworkRunning, TaskLifecycleState.StalledRework, true)]
    [InlineData(TaskEvent.ReworkFired, TaskLifecycleState.StalledRework, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.ConflictDetected, TaskLifecycleState.MergeReady, TaskLifecycleState.ReworkQueued, true)]
    [InlineData(TaskEvent.CiGreen, TaskLifecycleState.PROpen, TaskLifecycleState.MergeReady, true)]
    [InlineData(TaskEvent.Merged, TaskLifecycleState.MergeReady, TaskLifecycleState.Merged, true)]
    [InlineData(TaskEvent.BreakerTripped, TaskLifecycleState.StalledRework, TaskLifecycleState.Failed, true)]
    [InlineData(TaskEvent.OperatorRequeue, TaskLifecycleState.Failed, TaskLifecycleState.Pending, true)]
    // Illegal: these are the corruption shapes the table exists to flag.
    [InlineData(TaskEvent.Merged, TaskLifecycleState.Pending, TaskLifecycleState.Merged, false)]
    [InlineData(TaskEvent.PrOpened, TaskLifecycleState.Merged, TaskLifecycleState.PROpen, false)]
    [InlineData(TaskEvent.ParkedOnInfra, TaskLifecycleState.Merged, TaskLifecycleState.ParkedInfra, false)]
    [InlineData(TaskEvent.Dispatched, TaskLifecycleState.PROpen, TaskLifecycleState.Dispatching, false)]
    public void TransitionTable_Legality(TaskEvent evt, TaskLifecycleState from, TaskLifecycleState to, bool expected)
    {
        Assert.Equal(expected, TaskStateMachine.IsLegal(evt, from, to));
    }

    [Fact]
    public void IdempotentReobservation_InTable()
    {
        // Self-transitions are legal ONLY when the table lists them
        // (idempotent re-observation of the same reality).
        Assert.True(TaskStateMachine.IsLegal(TaskEvent.ReworkFired, TaskLifecycleState.ReworkQueued, TaskLifecycleState.ReworkQueued));
        Assert.True(TaskStateMachine.IsLegal(TaskEvent.PrOpened, TaskLifecycleState.PROpen, TaskLifecycleState.PROpen));
        Assert.False(TaskStateMachine.IsLegal(TaskEvent.Merged, TaskLifecycleState.Pending, TaskLifecycleState.Pending));
    }

    [Fact]
    public async Task Report_LegalTransition_RecordsStateMetadata()
    {
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var next = await Machine().ReportAsync(task, TaskEvent.Dispatched, null, false, CancellationToken.None);

        Assert.Equal(TaskLifecycleState.Dispatching, next);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("Dispatching", after.GetMetadata("state"));
        Assert.Equal("Dispatched", after.GetMetadata("lastEvent"));
        Assert.NotNull(after.GetMetadata("stateEnteredAt"));
        Assert.Null(after.GetMetadata("stateViolation"));
    }

    [Fact]
    public async Task Report_IllegalTransition_ShadowMode_AllowsAndFlags()
    {
        // A Pending task reporting Merged (impossible in reality) —
        // shadow mode: allowed, flagged, state unchanged.
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var next = await Machine(authority: false).ReportAsync(task, TaskEvent.Merged, null, false, CancellationToken.None);

        Assert.Equal(TaskLifecycleState.Pending, next);   // no move
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("Pending+Merged", after.GetMetadata("stateViolation"));
    }

    [Fact]
    public async Task Report_IllegalTransition_AuthorityMode_FlagsWithoutThrowing()
    {
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var next = await Machine(authority: true).ReportAsync(task, TaskEvent.Merged, null, false, CancellationToken.None);
        Assert.Equal(TaskLifecycleState.Pending, next);
        Assert.Equal("Pending+Merged", (await _issues.GetAsync(task.Id))!.GetMetadata("stateViolation"));
    }

    [Fact]
    public async Task Report_RecordedState_WinsOverStaleFlagDerivation()
    {
        // Observed live 2026-07-26 (task-183): after a rework round
        // pushes, reworkReason persists on the task, so flag-
        // derivation keeps saying ReworkRunning and the next verdict
        // flags a false violation. The machine must trust its own
        // recorded state (PROpen after the push) instead.
        var task = await SeedTaskAsync(IssueStatus.InProgress, new()
        {
            ["prNumber"] = "47",
            ["reworkReason"] = "CI failed",          // stale flag
            ["reworkAttempts"] = "1",
            ["state"] = "PROpen",                     // machine record (post-push)
        });
        var next = await Machine().ReportAsync(task, TaskEvent.ReviewApproved, null, false, CancellationToken.None);

        Assert.Equal(TaskLifecycleState.MergeReady, next);
        Assert.Null((await _issues.GetAsync(task.Id))!.GetMetadata("stateViolation"));
        Assert.Equal("MergeReady", (await _issues.GetAsync(task.Id))!.GetMetadata("state"));
    }

    [Fact]
    public async Task Report_RealisticSequence_TracksThrough()
    {
        // Dispatch -> PR open -> CI red -> rework -> push (head
        // moved) -> green -> merged: the canonical happy path.
        var task = await SeedTaskAsync(IssueStatus.Pending);
        var m = Machine();

        Assert.Equal(TaskLifecycleState.Dispatching,
            await m.ReportAsync(await Reload(task), TaskEvent.Dispatched, null, false, CancellationToken.None));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null, new Dictionary<string, object> { ["prNumber"] = "99" });
        Assert.Equal(TaskLifecycleState.PROpen,
            await m.ReportAsync(await Reload(task), TaskEvent.PrOpened, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.ReworkQueued,
            await m.ReportAsync(await Reload(task), TaskEvent.CiRedOnPr, null, false, CancellationToken.None));
        // The rework round: claimed (Dispatched), pushes (PrOpened —
        // production observes it via the dispatch-completed report).
        Assert.Equal(TaskLifecycleState.Dispatching,
            await m.ReportAsync(await Reload(task), TaskEvent.Dispatched, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.PROpen,
            await m.ReportAsync(await Reload(task), TaskEvent.PrOpened, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.MergeReady,
            await m.ReportAsync(await Reload(task), TaskEvent.CiGreen, null, false, CancellationToken.None));
        Assert.Equal(TaskLifecycleState.Merged,
            await m.ReportAsync(await Reload(task), TaskEvent.Merged, null, false, CancellationToken.None));
    }

    private async Task<IssueRecord> Reload(IssueRecord t) => (await _issues.GetAsync(t.Id))!;

    /// <summary>
    /// Exhaustive static table properties — the deterministic Phase
    /// 3 gate. The table is the contract the collapse relies on;
    /// these invariants must hold for it to be safe.
    /// </summary>
    [Fact]
    public void ExhaustiveTable_StaticInvariants()
    {
        var states = Enum.GetValues<TaskLifecycleState>();
        var events = Enum.GetValues<TaskEvent>();
        var terminal = new[]
        {
            TaskLifecycleState.Merged, TaskLifecycleState.Completed,
            TaskLifecycleState.Closed,
        };

        var legalPairs = 0;
        foreach (var from in states)
        {
            var outgoing = events.Count(evt => ProbeTo(evt, from));
            legalPairs += outgoing;

            if (terminal.Contains(from))
            {
                // Terminal states: only idempotent self-reports
                // (after-the-fact observation), never a transition
                // OUT to a different state.
                var exits = events.Where(evt => ProbeTo(evt, from))
                    .Where(evt => !TaskStateMachine.IsLegal(evt, from, from))
                    .ToList();
                Assert.True(exits.Count == 0,
                    $"terminal state {from} must not exit to another state, exits via [{string.Join(",", exits)}]");
            }
            else if (from is TaskLifecycleState.Failed or TaskLifecycleState.BlockedOperator)
            {
                // Operator-terminal: exactly one way out — the
                // operator requeue. Nothing else may leave.
                var exits = events.Where(evt => ProbeTo(evt, from)).ToList();
                Assert.True(exits.Count == 1 && exits[0] == TaskEvent.OperatorRequeue,
                    $"{from} must exit only via OperatorRequeue, has [{string.Join(",", exits)}]");
            }
            else
            {
                Assert.True(outgoing > 0, $"working state {from} has no outgoing events");
            }
        }

        // Every event is reachable from at least one state.
        foreach (var evt in events)
        {
            Assert.True(states.Any(s => ProbeTo(evt, s)),
                $"event {evt} is unreachable (no legal source state)");
        }

        Assert.True(legalPairs > 30, $"expected a non-trivial table, got {legalPairs} entries");
    }

    /// <summary>Whether the table has ANY entry for (evt, from).</summary>
    private static bool ProbeTo(TaskEvent evt, TaskLifecycleState from)
    {
        foreach (var to in Enum.GetValues<TaskLifecycleState>())
        {
            if (TaskStateMachine.IsLegal(evt, from, to)) return true;
        }
        return false;
    }
}
