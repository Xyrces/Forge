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

/// <summary>The phase-2 trigger: FailureTriageConsumer publishes
/// TriageRequested only when the project flag is ON and the guardrails
/// allow; at-cap / burn-loop failures park deterministically with no
/// event and no LLM. Flag off = zero behavior change (plan §2).</summary>
public sealed class FailureTriageKickTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly InMemoryTransport _transport = new();
    private readonly IssueStore _issues;
    private readonly FailureTriageStore _triage;
    private readonly FailureTriageConsumer _consumer;

    public FailureTriageKickTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("triage-kick");
        var publisher = new TalariaEventPublisher(_transport, NullLogger<TalariaEventPublisher>.Instance);
        _issues = new IssueStore(_dbPath, "proj", publisher);
        _triage = new FailureTriageStore(_issues);

        var factory = new ProjectContextFactory(
            new List<ProjectOptions>
            {
                new() { Id = "proj", Name = "Proj", Root = Path.GetDirectoryName(_dbPath)!, TriageEnabled = true },
            },
            new Dictionary<string, string> { ["proj"] = _dbPath });
        _consumer = new FailureTriageConsumer(
            _transport, factory, publisher, NullLogger<FailureTriageConsumer>.Instance);
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

    private async Task<string> FailTask()
    {
        var task = await _issues.CreateAsync(new NewIssue("task", "t"));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null);
        await _issues.TransitionAsync(task.Id, IssueStatus.Failed, "HTTP 429 rate limit (quota)");
        return task.Id;
    }

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

    [Fact]
    public async Task FlagOn_Failure_PublishesTriageRequested()
    {
        var taskId = await FailTask();

        var msgs = await UntilAsync(
            () => _transport.ReadAllFromTopicAsync<TriageRequested>(Topics.TriageRequested),
            m => m.Count > 0, "TriageRequested on the topic");
        Assert.Contains(msgs, m => m.Payload.TaskId == taskId && m.Payload.ProjectId == "proj");
    }

    [Fact]
    public async Task DailyCapReached_ParksDeterministically_NoEvent()
    {
        var taskId = await FailTask();
        // Sequencing: wait until failure #1 is fully handled (row open
        // + the kick event landed) BEFORE seeding the cap history —
        // otherwise the consumer can process failure #1 with the cap
        // already tripped and the test's premise inverts.
        await UntilAsync(() => _triage.GetOpenForTaskAsync(taskId), r => r is not null, "row for failure #1");
        await UntilAsync(
            () => _transport.ReadAllFromTopicAsync<TriageRequested>(Topics.TriageRequested),
            m => m.Count == 1, "TriageRequested for failure #1");
        // Two triage actions already today → the next failure is at cap.
        for (var i = 0; i < 2; i++)
        {
            var rowId = await _triage.OpenAsync(taskId, DateTime.UtcNow.AddHours(-(i + 2)), "llm-429-quota", "transient-upstream", null);
            await _triage.RecordActionAsync(rowId, FailureTriageActions.TriageRequeue,
                FailureTriageActors.Triage, DateTime.UtcNow.AddHours(-(i + 1)), FailureTriageOutcomes.FailedAgain);
        }

        await _issues.TransitionAsync(taskId, IssueStatus.InProgress, null);
        await _issues.TransitionAsync(taskId, IssueStatus.Failed, "HTTP 429 rate limit (quota)");

        // The freshly re-failed row parks deterministically… (probe the
        // TASK metadata — the ledger row is written before the metadata
        // stamp inside the park, so the row alone is a partial read)
        var parkedTask = await UntilAsync(async () => await _issues.GetAsync(taskId),
            t => t?.GetMetadata("triageAction") == "parked", "parked metadata stamp");
        Assert.Contains("daily triage action cap", parkedTask.GetMetadata("triageNote"));
        var parked = (await _triage.ListForTaskAsync(taskId))
            .First(r => r.Action == FailureTriageActions.TriagePark);
        Assert.Equal(FailureTriageActors.Triage, parked.Actor);
        Assert.Null(parked.Outcome);

        // …and NO further TriageRequested follows the at-cap failure:
        // the test-reader group's offset drained at the first read, so
        // only NEW messages would appear here.
        await Task.Delay(500);
        var msgs = await _transport.ReadAllFromTopicAsync<TriageRequested>(Topics.TriageRequested);
        Assert.Empty(msgs);
    }

    [Fact]
    public async Task BurnLoop_ParksDeterministically_NoSecondRun()
    {
        var taskId = await FailTask();
        // Same sequencing rule: failure #1 fully handled (row open +
        // kick landed, history empty) before the burn history seeds.
        await UntilAsync(() => _triage.GetOpenForTaskAsync(taskId), r => r is not null, "row for failure #1");
        await UntilAsync(
            () => _transport.ReadAllFromTopicAsync<TriageRequested>(Topics.TriageRequested),
            m => m.Count == 1, "TriageRequested for failure #1");
        // Two prior triage requeues on this signature, neither succeeded.
        for (var i = 0; i < 2; i++)
        {
            var rowId = await _triage.OpenAsync(taskId, DateTime.UtcNow.AddDays(-(i + 2)), "llm-429-quota", "transient-upstream", null);
            await _triage.RecordActionAsync(rowId, FailureTriageActions.TriageRequeue,
                FailureTriageActors.Triage, DateTime.UtcNow.AddDays(-(i + 2)).AddMinutes(5), FailureTriageOutcomes.FailedAgain);
        }

        await _issues.TransitionAsync(taskId, IssueStatus.InProgress, null);
        await _issues.TransitionAsync(taskId, IssueStatus.Failed, "HTTP 429 rate limit (quota)");

        var burnTask = await UntilAsync(async () => await _issues.GetAsync(taskId),
            t => t?.GetMetadata("triageAction") == "parked", "burn-loop park stamp");
        Assert.Contains("without success", burnTask.GetMetadata("triageNote"));
        var parked2 = (await _triage.ListForTaskAsync(taskId))
            .First(r => r.Action == FailureTriageActions.TriagePark);
        Assert.Equal(FailureTriageActors.Triage, parked2.Actor);
    }
}
