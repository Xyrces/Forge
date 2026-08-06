using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// OrphanedClaimReaper: InProgress + assignee + no active run →
/// requeued Pending with the recovery budget consumed; genuinely
/// running, watch-owned, and budget-exhausted tasks untouched.
/// Escalation: warn findings older than a day bump to fail.
/// </summary>
public class OrphanedClaimReaperTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly AgentRunStore _runs;

    public OrphanedClaimReaperTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-reap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _runs = new AgentRunStore(_issues.Db);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private async Task<IssueRecord> ClaimedAsync(Dictionary<string, object>? meta = null)
    {
        var t = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "work", Metadata: meta));
        await _issues.ClaimAsync(t.Id, "forge");
        return (await _issues.GetAsync(t.Id))!;
    }

    [Fact]
    public async Task OrphanedClaim_RequeuedWithBudgetConsumed()
    {
        var t = await ClaimedAsync();

        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new[] { t.Id }, reaped);
        var after = (await _issues.GetAsync(t.Id))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        Assert.Equal(1, after.RecoveryAttempts);
        var events = await _issues.ListEventsAsync(t.Id, 1);
        Assert.Contains("orphaned claim requeued", events[0].Detail);
    }

    [Fact]
    public async Task ActiveRun_NotTouched()
    {
        var t = await ClaimedAsync();
        await _runs.StartAsync("run1", t.Id, "CoreDev", "m", CancellationToken.None);

        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(reaped);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(t.Id))!.Status);
    }

    [Fact]
    public async Task WatchOwned_NotTouched()
    {
        var t = await ClaimedAsync(meta: new() { ["prNumber"] = "42" });

        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(reaped);
    }

    [Fact]
    public async Task WatchOwned_PROpenState_NotTouched()
    {
        var t = await ClaimedAsync(meta: new()
        {
            ["prNumber"] = "42",
            ["state"] = nameof(TaskLifecycleState.PROpen),
        });

        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(reaped);
    }

    [Fact]
    public async Task ReworkQueuedWithPr_Reaped_EngineeringOwesIt()
    {
        // Live 2026-07-31: task-360/361/362/364 sat InProgress +
        // ReworkQueued + prNumber with no run after a restart — the
        // blanket prNumber exclusion stranded them forever.
        var t = await ClaimedAsync(meta: new()
        {
            ["prNumber"] = "758",
            ["state"] = nameof(TaskLifecycleState.ReworkQueued),
        });

        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new[] { t.Id }, reaped);
        var after = (await _issues.GetAsync(t.Id))!;
        Assert.Equal(IssueStatus.Pending, after.Status);
        // The rework round's context survives the reap — the next
        // engineering claim must still see state + prNumber.
        Assert.Equal(nameof(TaskLifecycleState.ReworkQueued), after.GetMetadata("state"));
        Assert.Equal("758", after.GetMetadata("prNumber"));
    }

    [Fact]
    public async Task BudgetExhausted_NotTouched()
    {
        var t = await ClaimedAsync();
        for (var i = 0; i < OrphanedClaimReaper.MaxReapAttempts; i++)
        {
            await _issues.IncrementRecoveryAttemptsAsync(t.Id);
        }

        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(reaped);
        Assert.Equal(IssueStatus.InProgress, (await _issues.GetAsync(t.Id))!.Status);
    }

    [Fact]
    public async Task FreshClaim_NotTouched()
    {
        var t = await ClaimedAsync();

        // Default 30-minute threshold: a just-claimed task is not an orphan.
        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: null, CancellationToken.None);

        Assert.Empty(reaped);
    }

    [Fact]
    public async Task Escalation_WarnFindingOlderThanADay_BumpsToFail()
    {
        var store = new WatchdogFindingStore(_issues);
        var finding = new WatchdogScanner.Finding(WatchdogScanner.StuckSprint, "sprint-1", "warn", "detail");
        await store.SyncAsync(new[] { finding }, DateTime.UtcNow, CancellationToken.None);

        await store.SyncAsync(new[] { finding }, DateTime.UtcNow.AddHours(25), CancellationToken.None);

        var open = await store.ListOpenAsync(CancellationToken.None);
        Assert.Single(open);
        Assert.Equal("fail", open[0].Severity);
    }

    [Fact]
    public async Task FailZombieRuns_NullThreshold_ClosesAllRunning()
    {
        await _runs.StartAsync("r1", "task-1", "Reviewer", "m", CancellationToken.None);
        await _runs.StartAsync("r2", "task-2", "Reviewer", "m", CancellationToken.None);

        var closed = await _runs.FailZombieRunsAsync(null, "restart", CancellationToken.None);

        Assert.Equal(2, closed.Count);
        Assert.Empty(await _runs.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailZombieRuns_StaleThreshold_OnlyClosesOld()
    {
        await _runs.StartAsync("fresh", "task-1", "Reviewer", "m", CancellationToken.None);

        // Threshold in the future: even the just-started row is stale.
        var closed = await _runs.FailZombieRunsAsync(
            DateTime.UtcNow.AddMinutes(1), "stale", CancellationToken.None);
        Assert.Equal(new[] { "fresh" }, closed);

        // Threshold in the past: nothing is stale.
        var none = await _runs.FailZombieRunsAsync(
            DateTime.UtcNow.AddMinutes(-1), "stale", CancellationToken.None);
        Assert.Empty(none);
    }

    [Fact]
    public async Task StartAsync_DispatchId_PersistedAndReadBack()
    {
        // v30: the dispatch correlation id rides the run row so
        // journal + run + task join on one id in a postmortem.
        await _runs.StartAsync("r1", "task-1", "CoreDev", "m", CancellationToken.None, dispatchId: "d-abc12345");
        await _runs.StartAsync("r2", "task-2", "Reviewer", "m", CancellationToken.None);

        var active = await _runs.ListActiveAsync(CancellationToken.None);
        Assert.Equal("d-abc12345", active.Single(r => r.Id == "r1").DispatchId);
        Assert.Null(active.Single(r => r.Id == "r2").DispatchId);
    }

    [Fact]
    public async Task ZombieRunRow_NoLongerShieldsOrphanedClaim()
    {
        // Live 2026-08-01: a dead run's un-closed agent_run row read
        // as "active", so the reaper skipped its orphaned claim
        // forever. The watchdog closes zombies BEFORE reaping.
        var t = await ClaimedAsync();
        await _runs.StartAsync("zombie", t.Id, "Reviewer", "m", CancellationToken.None);
        var shielded = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);
        Assert.Empty(shielded);

        await _runs.FailZombieRunsAsync(null, "restart", CancellationToken.None);
        var reaped = await OrphanedClaimReaper.ReapAsync(
            _issues, _runs, DateTime.UtcNow, orphanAfter: TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(new[] { t.Id }, reaped);
    }
}
