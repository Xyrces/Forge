using Forge.Configuration;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Messaging;
using Forge.Orchestrator.Consumers;
using Forge.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests.Messaging;

/// <summary>FailureTriageConsumer over the real in-memory transport:
/// IssueStore choke-point publication → forge.task-failure-signal →
/// ledger row lifecycle.</summary>
public sealed class FailureTriageConsumerTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly InMemoryTransport _transport = new();
    private readonly IssueStore _issues;
    private readonly FailureTriageStore _triage;
    private readonly FailureTriageConsumer _consumer;

    public FailureTriageConsumerTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("triage-consumer");
        var publisher = new TalariaEventPublisher(_transport, NullLogger<TalariaEventPublisher>.Instance);
        _issues = new IssueStore(_dbPath, "proj", publisher);
        _triage = new FailureTriageStore(_issues);

        var factory = new ProjectContextFactory(
            new List<ProjectOptions> { new() { Id = "proj", Name = "Proj", Root = Path.GetDirectoryName(_dbPath)! } },
            new Dictionary<string, string> { ["proj"] = _dbPath });
        _consumer = new FailureTriageConsumer(
            _transport, factory, NullLogger<FailureTriageConsumer>.Instance);
    }

    public async Task InitializeAsync() => await _consumer.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _consumer.StopAsync(CancellationToken.None);
        _consumer.Dispose();
        _issues.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private async Task<IssueRecord> NewTask(string title = "t")
        => await _issues.CreateAsync(new NewIssue(Type: "task", Title: title));

    private static async Task<T> UntilAsync<T>(Func<Task<T>> probe, Func<T, bool> pred, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var v = await probe();
            if (pred(v)) return v;
            await Task.Delay(50);
        }
        throw new TimeoutException($"timed out waiting for {what}");
    }

    private Task<FailureTriageEntry?> OpenRow(string taskId)
        => UntilAsync<FailureTriageEntry?>(() => _triage.GetOpenForTaskAsync(taskId), r => r is not null, $"open row for {taskId}");

    [Fact]
    public async Task FailureTransition_OpensClassifiedLedgerRow()
    {
        var task = await NewTask();
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "HttpRequestException: HTTP 429 rate limit (quota): too many requests");

        var row = await OpenRow(task.Id);
        Assert.NotNull(row);
        Assert.Equal("llm-429-quota", row!.Signature);
        Assert.Equal("transient-upstream", row.Classification);
        Assert.Contains("429", row.ErrorExcerpt);
        Assert.Null(row.Action);
        Assert.Null(row.Outcome);
    }

    [Fact]
    public async Task OperatorRequeue_RecordsAction_PendingOutcome()
    {
        var task = await NewTask();
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "agent produced no diff in 3 attempts (last response truncated)");
        var row = await OpenRow(task.Id);
        Assert.Equal("no-diff-bounce", row!.Signature);

        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, "operator requeue from Failed",
            new Dictionary<string, object> { ["clearanceAction"] = "operator-requeue" });

        await UntilAsync(() => _triage.GetOpenForTaskAsync(task.Id),
            r => r is { Action: not null }, "recorded clearance action");
        var actioned = await _triage.GetOpenForTaskAsync(task.Id);
        Assert.Equal(FailureTriageActions.OperatorRequeue, actioned!.Action);
        Assert.Equal("operator", actioned.Actor);
        Assert.NotNull(actioned.ActedAt);
        Assert.Equal(FailureTriageOutcomes.Pending, actioned.Outcome);
    }

    [Fact]
    public async Task RedispatchSuccess_ClosesOutcome_Succeeded()
    {
        var task = await NewTask();
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "HTTP 503 from kilo gateway");
        await OpenRow(task.Id);
        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, "operator requeue",
            new Dictionary<string, object>
            {
                ["clearanceAction"] = "operator-requeue",
                ["state"] = "Pending",
                ["stateEnteredAt"] = DateTime.UtcNow.ToString("O"),
            });
        await UntilAsync(() => _triage.GetOpenForTaskAsync(task.Id),
            r => r is { Outcome: FailureTriageOutcomes.Pending }, "pending outcome");

        // The redispatch produced a PR: machine records PROpen.
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object>
            {
                ["state"] = "PROpen",
                ["stateEnteredAt"] = DateTime.UtcNow.ToString("O"),
            });

        await UntilAsync(async () => (await _triage.ListAsync()).Single().Outcome,
            o => o == FailureTriageOutcomes.Succeeded, "succeeded outcome");
    }

    [Fact]
    public async Task SameSignatureRefailure_ClosesFailedAgain_AndReopens()
    {
        var task = await NewTask();
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "HTTP 429 rate limit (quota)");
        await OpenRow(task.Id);
        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, "operator requeue",
            new Dictionary<string, object> { ["clearanceAction"] = "operator-requeue" });
        await UntilAsync(() => _triage.GetOpenForTaskAsync(task.Id),
            r => r is { Outcome: FailureTriageOutcomes.Pending }, "pending outcome");

        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "HTTP 429 rate limit (quota)");

        await UntilAsync(() => _triage.ListAsync(), rows => rows.Count == 2, "two ledger rows");
        var rows = await _triage.ListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("llm-429-quota", r.Signature));
        Assert.Contains(rows, r => r.Outcome == FailureTriageOutcomes.FailedAgain);
        var open = await _triage.GetOpenForTaskAsync(task.Id);
        Assert.NotNull(open);
        Assert.Null(open!.Action);
        Assert.Null(open.Outcome);
    }

    [Fact]
    public async Task OperatorClose_RecordsCloseAction_NullOutcome()
    {
        var task = await NewTask();
        await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, "circuit breaker tripped after 3 strikes");
        var row = await OpenRow(task.Id);
        Assert.Equal("breaker-exhausted", row!.Signature);

        await _issues.TransitionAsync(task.Id, IssueStatus.Closed, "operator close: obsolete",
            new Dictionary<string, object> { ["clearanceAction"] = "operator-close" });

        await UntilAsync(async () => (await _triage.ListAsync()).Single().Action,
            a => a == FailureTriageActions.OperatorClose, "close action");
        var closed = (await _triage.ListAsync()).Single();
        Assert.Null(closed.Outcome);
        Assert.Null(await _triage.GetOpenForTaskAsync(task.Id));
    }
}
