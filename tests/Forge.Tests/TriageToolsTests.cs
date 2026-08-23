using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

/// <summary>The triage agent's bounded action space (phase 2, plan §3):
/// every action writes the ledger with actor=triage and stamps the
/// task's triageAction/triageNote metadata; store guards make each
/// idempotent against double-fire.</summary>
public sealed class TriageToolsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly FailureTriageStore _triage;
    private readonly TriageTools _tools;

    public TriageToolsTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("triage-tools");
        _issues = new IssueStore(_dbPath);
        _triage = new FailureTriageStore(_issues);
        _tools = new TriageTools(_issues, _triage, lifecycle: null,
            NullLogger<TriageTools>.Instance);
    }

    public void Dispose()
    {
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private async Task<IssueRecord> FailedTaskWithOpenRow(string signature = "llm-429-quota")
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "failing task"));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "HTTP 429 rate limit (quota)",
            new Dictionary<string, object> { ["retryCount"] = "2", ["noProgressAttempts"] = "1" });
        await _triage.OpenAsync(task.Id, DateTime.UtcNow, signature, "transient-upstream", "HTTP 429");
        return task;
    }

    [Fact]
    public async Task RequeueWithGuidance_ActionsRowAsTriage_TransitionsToPending_StampsMetadata()
    {
        var task = await FailedTaskWithOpenRow();

        var result = await _tools.RequeueWithGuidanceAsync(
            task.Id, "llm-429-quota", "HTTP 429 is transient quota pressure — retry unchanged", "HTTP 429 rate limit (quota)");

        Assert.StartsWith("ok:", result);
        var row = await _triage.GetOpenForTaskAsync(task.Id);
        Assert.NotNull(row);
        Assert.Equal(FailureTriageActions.TriageRequeue, row!.Action);
        Assert.Equal(FailureTriageActors.Triage, row.Actor);
        Assert.Equal(FailureTriageOutcomes.Pending, row.Outcome);

        var after = await _issues.GetAsync(task.Id);
        Assert.Equal(IssueStatus.Pending, after!.Status);
        Assert.Equal("requeue", after.GetMetadata("triageAction"));
        Assert.Contains("transient quota", after.GetMetadata("triageNote"));
        Assert.Equal("triage: llm-429-quota", after.GetMetadata("reworkReason"));
        Assert.Contains("retry unchanged", after.GetMetadata("reworkContext"));
        Assert.NotNull(after.GetMetadata("requeuedFromFailedAt"));
        Assert.Null(after.GetMetadata("lastError"));
        // Triage requeues consume rounds deliberately (plan §4): the
        // strike budget is NOT reset.
        Assert.Equal("2", after.GetMetadata("retryCount"));
        Assert.Equal("1", after.GetMetadata("noProgressAttempts"));
    }

    [Fact]
    public async Task RequeueWithGuidance_Twice_IsIdempotent()
    {
        var task = await FailedTaskWithOpenRow();
        await _tools.RequeueWithGuidanceAsync(task.Id, "llm-429-quota", "first", null);

        // The row is actioned and the task is Pending now — a second
        // call refuses on both guards.
        var second = await _tools.RequeueWithGuidanceAsync(task.Id, "llm-429-quota", "second", null);
        Assert.StartsWith("error:", second);
        var rows = await _triage.ListForTaskAsync(task.Id);
        Assert.Single(rows, r => r.Action == FailureTriageActions.TriageRequeue);
    }

    [Fact]
    public async Task ParkForOperator_ActionsRow_TaskStaysFailed()
    {
        var task = await FailedTaskWithOpenRow("breaker-exhausted");

        var result = await _tools.ParkForOperatorAsync(task.Id, "three strikes — judgment call");

        Assert.StartsWith("ok:", result);
        var rows = await _triage.ListForTaskAsync(task.Id);
        var row = Assert.Single(rows);
        Assert.Equal(FailureTriageActions.TriagePark, row.Action);
        Assert.Equal(FailureTriageActors.Triage, row.Actor);
        Assert.Null(row.Outcome);
        // Parked rows are no longer open (action set, outcome null).
        Assert.Null(await _triage.GetOpenForTaskAsync(task.Id));

        var after = await _issues.GetAsync(task.Id);
        Assert.Equal(IssueStatus.Failed, after!.Status);
        Assert.Equal("parked", after.GetMetadata("triageAction"));
        Assert.Equal("three strikes — judgment call", after.GetMetadata("triageNote"));
    }

    [Fact]
    public async Task FlagBugSuspect_ActionsRow_LedgerFlagOnly()
    {
        var task = await FailedTaskWithOpenRow("merged-tarpit");

        var result = await _tools.FlagBugSuspectAsync(
            task.Id, "merged-tarpit", "the same signature hit 4 distinct tasks — product bug suspect");

        Assert.StartsWith("ok:", result);
        var rows = await _triage.ListForTaskAsync(task.Id);
        var row = Assert.Single(rows);
        Assert.Equal(FailureTriageActions.TriageFlagBug, row.Action);
        Assert.Equal(FailureTriageActors.Triage, row.Actor);

        var after = await _issues.GetAsync(task.Id);
        Assert.Equal(IssueStatus.Failed, after!.Status);
        Assert.Equal("flag-bug", after.GetMetadata("triageAction"));
        Assert.Contains("product bug suspect", after.GetMetadata("triageNote"));
        // No issue creation (operator constraint): the store holds
        // exactly the one task.
        Assert.Single(await _issues.ListAsync(new IssueFilter()));
    }

    [Fact]
    public async Task Actions_OnTaskWithoutOpenRow_Refuse()
    {
        var task = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "never failed"));
        Assert.StartsWith("error:", await _tools.ParkForOperatorAsync(task.Id, "nothing to park"));
        Assert.StartsWith("error:", await _tools.FlagBugSuspectAsync(task.Id, "other", "none"));
        Assert.StartsWith("error:", await _tools.RequeueWithGuidanceAsync(task.Id, "other", "none", null));
    }
}
