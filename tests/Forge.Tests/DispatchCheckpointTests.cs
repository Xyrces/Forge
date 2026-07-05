using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P4 Stage A.1 — schema v11 + dispatch_checkpoint +
/// recovery_report table + IssueStore helpers. See
/// docs/p4-restart-safety.md.
/// </summary>
public class DispatchCheckpointTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly RecoveryReportStore _reports;

    public DispatchCheckpointTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _reports = new RecoveryReportStore(Path.Combine(_workDir, "issues.db"));
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void SchemaVersion_IsEleven_AfterMigration()
    {
        Assert.Equal(11, IssueStore.CurrentSchemaVersion);
    }

    [Fact]
    public void DispatchCheckpoint_RoundTripsThroughDbValue()
    {
        foreach (var c in Enum.GetValues<DispatchCheckpoint>())
        {
            DispatchCheckpointExtensions.TryParseDb(c.ToDbValue(), out var parsed);
            Assert.Equal(c, parsed);
        }
    }

    [Fact]
    public async Task ClaimAsync_SetsCheckpointClaimed()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        var claimed = await _issues.ClaimAsync(issue.Id, "kilo");
        Assert.NotNull(claimed);
        Assert.Equal(DispatchCheckpoint.Claimed, claimed!.DispatchCheckpoint);
        Assert.NotNull(claimed.CheckpointAt);
        Assert.Equal(0, claimed.RecoveryAttempts);
    }

    [Fact]
    public async Task SetCheckpointAsync_AdvancesThroughAllStates()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "kilo");

        foreach (var cp in new[] {
            DispatchCheckpoint.WorktreeAcquired,
            DispatchCheckpoint.AgentCompleted,
            DispatchCheckpoint.CommitDone,
            DispatchCheckpoint.PushDone,
            DispatchCheckpoint.PrOpened,
        })
        {
            await _issues.SetCheckpointAsync(issue.Id, cp);
            var fetched = await _issues.GetAsync(issue.Id);
            Assert.Equal(cp, fetched!.DispatchCheckpoint);
            Assert.NotNull(fetched.CheckpointAt);
        }
    }

    [Fact]
    public async Task TransitionAsync_TerminalStatus_ClearsCheckpoint()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "kilo");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted);
        await _issues.TransitionAsync(issue.Id, IssueStatus.Failed, "test failure");
        var after = await _issues.GetAsync(issue.Id);
        Assert.Null(after!.DispatchCheckpoint);
        Assert.Null(after.CheckpointAt);
    }

    [Fact]
    public async Task ListInProgressForRecoveryAsync_ReturnsOnlyKiloInProgress()
    {
        // Three candidates: kilo+InProgress (should appear),
        // human+InProgress (should NOT appear), kilo+Pending (should NOT appear).
        var kilo = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "k"));
        var human = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "h", Assignee: "human"));
        var pending = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "p"));

        await _issues.ClaimAsync(kilo.Id, "kilo");
        // Force the human-assigned issue into InProgress without
        // touching the checkpoint (mimics a manual state).
        var humanIssue = (await _issues.GetAsync(human.Id))!;
        await _issues.TransitionAsync(human.Id, IssueStatus.InProgress, "manual");

        var list = await _issues.ListInProgressForRecoveryAsync();
        Assert.Single(list);
        Assert.Equal(kilo.Id, list[0].Id);
        Assert.Equal(DispatchCheckpoint.Claimed, list[0].DispatchCheckpoint);
    }

    [Fact]
    public async Task IncrementRecoveryAttemptsAsync_IncrementsCounter()
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "kilo");
        var n = await _issues.IncrementRecoveryAttemptsAsync(issue.Id);
        Assert.Equal(1, n);
        var after = await _issues.GetAsync(issue.Id);
        Assert.Equal(1, after!.RecoveryAttempts);
        await _issues.IncrementRecoveryAttemptsAsync(issue.Id);
        after = await _issues.GetAsync(issue.Id);
        Assert.Equal(2, after!.RecoveryAttempts);
    }

    [Fact]
    public async Task RecoveryReportStore_StartFinish_RoundTrip()
    {
        var started = await _reports.StartAsync(specId: null);
        Assert.True(started.Id > 0);
        var actions = new List<RecoveryActionRecord>
        {
            new("issue-1", "claimed", "worktree_acquired", "replay", null),
            new("issue-2", null, null, "failed", "worktree missing"),
        };
        var finished = await _reports.FinishAsync(
            started.Id,
            issuesScanned: 5,
            issuesReplayed: 1,
            issuesFailed: 1,
            actions,
            duration: TimeSpan.FromMilliseconds(123));
        Assert.Equal(5, finished.IssuesScanned);
        Assert.Equal(1, finished.IssuesReplayed);
        Assert.Equal(1, finished.IssuesFailed);
        Assert.Equal(123, finished.DurationMs);

        var fetched = await _reports.GetAsync(started.Id);
        Assert.NotNull(fetched);
        Assert.Equal(5, fetched!.IssuesScanned);
        // actions_json is a JSON array; verify the contents.
        Assert.Contains("\"IssueId\":\"issue-1\"", fetched.ActionsJson);
        Assert.Contains("\"Action\":\"replay\"", fetched.ActionsJson);
        Assert.Contains("\"Action\":\"failed\"", fetched.ActionsJson);
    }

    [Fact]
    public async Task RecoveryReportStore_ListAsync_ReturnsRecentFirst()
    {
        var r1 = await _reports.StartAsync(specId: null);
        var r2 = await _reports.StartAsync(specId: "spec-x");
        await _reports.FinishAsync(r1.Id, 0, 0, 0, Array.Empty<RecoveryActionRecord>(), TimeSpan.Zero);
        await _reports.FinishAsync(r2.Id, 0, 0, 0, Array.Empty<RecoveryActionRecord>(), TimeSpan.Zero);
        var list = await _reports.ListAsync();
        Assert.Equal(2, list.Count);
        // Most recent first (r2).
        Assert.Equal(r2.Id, list[0].Id);
        Assert.Equal("spec-x", list[0].SpecId);
    }

    [Fact]
    public async Task SchemaMigration_IsIdempotent()
    {
        // IssueStore ctor runs the migration every launch; running
        // it twice must not error and must not duplicate rows.
        await using var issues2 = new IssueStore(Path.Combine(_workDir, "issues.db"));
        await issues2.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        // Verify the recovery_report table is there by inserting a row.
        var r = await _reports.StartAsync(specId: null);
        Assert.True(r.Id > 0);
    }
}