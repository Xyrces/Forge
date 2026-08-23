using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class FailureTriageStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly FailureTriageStore _triage;

    public FailureTriageStoreTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("triage");
        _issues = new IssueStore(_dbPath);
        _triage = new FailureTriageStore(_issues);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Open_ThenGetOpenForTask_RoundTrips()
    {
        var failedAt = DateTime.UtcNow;
        var id = await _triage.OpenAsync("task-1", failedAt, "llm-429-quota", "transient-upstream", "HTTP 429 rate limit");

        var open = await _triage.GetOpenForTaskAsync("task-1");
        Assert.NotNull(open);
        Assert.Equal(id, open.Id);
        Assert.Equal("task-1", open.TaskId);
        Assert.Equal("llm-429-quota", open.Signature);
        Assert.Equal("transient-upstream", open.Classification);
        Assert.Equal("HTTP 429 rate limit", open.ErrorExcerpt);
        Assert.Null(open.Action);
        Assert.Null(open.Outcome);
        Assert.Equal(IssueStore.ParseTime(failedAt.ToString(IssueStore.DateFormat)), open.FailedAt);
    }

    [Fact]
    public async Task RecordAction_IsIdempotent_AndMarksPending()
    {
        var id = await _triage.OpenAsync("task-1", DateTime.UtcNow, "no-diff-bounce", "no-progress", null);
        var actedAt = DateTime.UtcNow;

        await _triage.RecordActionAsync(id, FailureTriageActions.OperatorRequeue, "operator", actedAt, FailureTriageOutcomes.Pending);
        // Redelivery: the action IS NULL guard refuses a second write.
        await _triage.RecordActionAsync(id, FailureTriageActions.OperatorClose, "operator", actedAt, null);

        var open = await _triage.GetOpenForTaskAsync("task-1");
        Assert.NotNull(open);
        Assert.Equal(FailureTriageActions.OperatorRequeue, open.Action);
        Assert.Equal("operator", open.Actor);
        Assert.NotNull(open.ActedAt);
        Assert.Equal(FailureTriageOutcomes.Pending, open.Outcome);
    }

    [Fact]
    public async Task CloseOutcome_Succeeds_Once()
    {
        var id = await _triage.OpenAsync("task-1", DateTime.UtcNow, "llm-429-quota", "transient-upstream", null);
        await _triage.RecordActionAsync(id, FailureTriageActions.OperatorRequeue, "operator", DateTime.UtcNow, FailureTriageOutcomes.Pending);

        await _triage.CloseOutcomeAsync(id, FailureTriageOutcomes.Succeeded);
        // Redelivery: outcome='pending' guard refuses the overwrite.
        await _triage.CloseOutcomeAsync(id, FailureTriageOutcomes.FailedAgain);

        Assert.Null(await _triage.GetOpenForTaskAsync("task-1"));
        var rows = await _triage.ListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(FailureTriageOutcomes.Succeeded, row.Outcome);
    }

    [Fact]
    public async Task OperatorClose_LeavesOutcomeNull_AndRowNoLongerOpen()
    {
        var id = await _triage.OpenAsync("task-1", DateTime.UtcNow, "other", "unclassified", null);
        await _triage.RecordActionAsync(id, FailureTriageActions.OperatorClose, "operator", DateTime.UtcNow, null);

        Assert.Null(await _triage.GetOpenForTaskAsync("task-1"));
        var row = Assert.Single(await _triage.ListAsync());
        Assert.Equal(FailureTriageActions.OperatorClose, row.Action);
        Assert.Null(row.Outcome);
    }

    [Fact]
    public async Task FailedAgain_Reopens_WithNewRowOnSameSignature()
    {
        var first = await _triage.OpenAsync("task-1", DateTime.UtcNow, "verification-fail", "verification", "first");
        await _triage.RecordActionAsync(first, FailureTriageActions.OperatorRequeue, "operator", DateTime.UtcNow, FailureTriageOutcomes.Pending);

        // The redispatch failed again: close the old row, open a new one.
        await _triage.CloseOutcomeAsync(first, FailureTriageOutcomes.FailedAgain);
        var second = await _triage.OpenAsync("task-1", DateTime.UtcNow, "verification-fail", "verification", "second");

        var open = await _triage.GetOpenForTaskAsync("task-1");
        Assert.NotNull(open);
        Assert.Equal(second, open.Id);
        Assert.Equal("second", open.ErrorExcerpt);

        var rows = await _triage.ListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(FailureTriageOutcomes.FailedAgain, rows.Single(r => r.Id == first).Outcome);
    }

    [Fact]
    public async Task UpdateOpen_Refreshes_UnclearedRow_Only()
    {
        var id = await _triage.OpenAsync("task-1", DateTime.UtcNow, "other", "unclassified", "stale");
        await _triage.UpdateOpenAsync(id, DateTime.UtcNow, "gateway-5xx", "transient-upstream", "fresh");

        var open = await _triage.GetOpenForTaskAsync("task-1");
        Assert.Equal("gateway-5xx", open!.Signature);
        Assert.Equal("fresh", open.ErrorExcerpt);

        // Once actioned, UpdateOpen no-ops (the row is no longer uncleared).
        await _triage.RecordActionAsync(id, FailureTriageActions.OperatorRequeue, "operator", DateTime.UtcNow, FailureTriageOutcomes.Pending);
        await _triage.UpdateOpenAsync(id, DateTime.UtcNow, "llm-429-quota", "transient-upstream", "nope");
        open = await _triage.GetOpenForTaskAsync("task-1");
        Assert.Equal("gateway-5xx", open!.Signature);
    }

    [Fact]
    public async Task ListAsync_SinceFilter_ExcludesOlderRows()
    {
        var old = DateTime.UtcNow.AddDays(-10);
        await _triage.OpenAsync("task-1", old, "other", "unclassified", null);
        await _triage.OpenAsync("task-2", DateTime.UtcNow, "gateway-5xx", "transient-upstream", null);

        var recent = await _triage.ListAsync(failedSince: DateTime.UtcNow.AddDays(-7));
        Assert.Single(recent);
        Assert.Equal("task-2", recent[0].TaskId);

        Assert.Equal(2, (await _triage.ListAsync()).Count);
    }
}
