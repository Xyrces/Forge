using Forge.Agents;
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

/// <summary>TriageConsumer over the real in-memory transport: hints are
/// re-derived (flag, open row, guardrails), the runner only runs when
/// everything allows, and every write lands in the store OWNED by the
/// event's project — never across the isolation boundary.</summary>
public sealed class TriageConsumerTests : IAsyncLifetime
{
    private sealed class FakeRunner : ITriageRunner
    {
        public List<(string ProjectId, string TaskId, string Signature)> Calls { get; } = new();
        public Task<TriageRunResult> RunAsync(string taskId, string signature, string classification, CancellationToken ct = default)
        {
            Calls.Add((ProjectId, taskId, signature));
            return Task.FromResult(new TriageRunResult(true, "requeue", "note", null));
        }
        public string ProjectId = "";
    }

    private readonly string _root;
    private readonly InMemoryTransport _transport = new();
    private readonly TalariaEventPublisher _publisher;
    private readonly FakeRunner _runner = new();
    private readonly TriageConsumer _consumer;
    private readonly Dictionary<string, (IssueStore Issues, FailureTriageStore Triage)> _stores = new();

    public TriageConsumerTests()
    {
        _root = TempRoot.Instance.NewDirectory("triage-consumer");
        _publisher = new TalariaEventPublisher(_transport, NullLogger<TalariaEventPublisher>.Instance);

        var projects = new List<ProjectOptions>
        {
            new() { Id = "on", Name = "On", Root = _root, TriageEnabled = true },
            new() { Id = "off", Name = "Off", Root = _root, TriageEnabled = false },
        };
        var dbByProject = new Dictionary<string, string>
        {
            ["on"] = Path.Combine(_root, "on.db"),
            ["off"] = Path.Combine(_root, "off.db"),
        };
        var factory = new ProjectContextFactory(projects, dbByProject);
        _consumer = new TriageConsumer(
            _transport, factory,
            ctx =>
            {
                _runner.ProjectId = ctx.Options.Id;
                return _runner;
            },
            NullLogger<TriageConsumer>.Instance);
    }

    public async Task InitializeAsync() => await _consumer.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _consumer.StopAsync(CancellationToken.None);
        _consumer.Dispose();
        foreach (var (issues, _) in _stores.Values) issues.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<(IssueStore Issues, FailureTriageStore Triage)> StoresFor(string projectId)
    {
        if (_stores.TryGetValue(projectId, out var s)) return s;
        var path = Path.Combine(_root, $"{projectId}.db");
        var issues = new IssueStore(path, projectId, _publisher);
        await Task.CompletedTask;
        var triage = new FailureTriageStore(issues);
        s = (issues, triage);
        _stores[projectId] = s;
        return s;
    }

    private async Task<string> FailedTaskWithOpenRow(string projectId, string signature = "llm-429-quota")
    {
        var (issues, triage) = await StoresFor(projectId);
        var task = await issues.CreateAsync(new NewIssue(Type: "task", Title: "t"));
        await issues.TransitionAsync(task.Id, IssueStatus.InProgress, null);
        await issues.TransitionAsync(task.Id, IssueStatus.Failed, "HTTP 429 rate limit (quota)");
        await triage.OpenAsync(task.Id, DateTime.UtcNow, signature, "transient-upstream", "HTTP 429");
        return task.Id;
    }

    private Task Publish(string projectId, string taskId)
    {
        var occurred = DateTimeOffset.UtcNow;
        return _publisher.PublishAsync(new TriageRequested
        {
            MessageId = TriageRequested.IdFor(projectId, taskId, occurred),
            ProjectId = projectId,
            TaskId = taskId,
            OccurredAt = occurred,
        });
    }

    private static async Task<T> UntilAsync<T>(Func<T> probe, Func<T, bool> pred, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var v = probe();
            if (pred(v)) return v;
            await Task.Delay(50);
        }
        throw new TimeoutException($"timed out waiting for {what}");
    }

    [Fact]
    public async Task FlagOff_DropsHint_RunnerNeverRuns()
    {
        var taskId = await FailedTaskWithOpenRow("off");
        await Publish("off", taskId);
        await Task.Delay(500);
        Assert.Empty(_runner.Calls);
        // The row stays open for the operator — untouched.
        var (_, triage) = await StoresFor("off");
        var open = await triage.GetOpenForTaskAsync(taskId);
        Assert.NotNull(open);
        Assert.Null(open!.Action);
    }

    [Fact]
    public async Task FlagOn_OpenRow_RunsRunner()
    {
        var taskId = await FailedTaskWithOpenRow("on");
        await Publish("on", taskId);

        var calls = await UntilAsync(() => _runner.Calls.ToList(), c => c.Count > 0, "runner call");
        Assert.Single(calls);
        Assert.Equal(("on", taskId, "llm-429-quota"), calls[0]);
    }

    [Fact]
    public async Task NoOpenRow_SkipsRunner()
    {
        var (issues, _) = await StoresFor("on");
        var task = await issues.CreateAsync(new NewIssue(Type: "task", Title: "never failed"));
        await Publish("on", task.Id);
        await Task.Delay(500);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task GuardrailTrip_ParksDeterministically_RunnerNeverRuns()
    {
        var taskId = await FailedTaskWithOpenRow("on");
        var (_, triage) = await StoresFor("on");
        // Burn history: two triage requeues on the same signature, no success.
        for (var i = 0; i < 2; i++)
        {
            var rowId = await triage.OpenAsync(taskId, DateTime.UtcNow.AddDays(-(i + 2)), "llm-429-quota", "transient-upstream", null);
            await triage.RecordActionAsync(rowId, FailureTriageActions.TriageRequeue,
                FailureTriageActors.Triage, DateTime.UtcNow.AddDays(-(i + 2)).AddMinutes(5), FailureTriageOutcomes.FailedAgain);
        }

        await Publish("on", taskId);

        var rows = await UntilAsync(() => triage.ListForTaskAsync(taskId).GetAwaiter().GetResult(),
            rs => rs.Any(r => r.Action == FailureTriageActions.TriagePark
                && r.Actor == FailureTriageActors.Triage), "deterministic park");
        var parked = rows.First(r => r.Action == FailureTriageActions.TriagePark);
        Assert.Null(parked.Outcome);
        await Task.Delay(300);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task EventForProjectB_WritesOnlyProjectBsStore()
    {
        // Same task id in BOTH stores (ids are per-store sequences) —
        // the event must resolve and write only the owning store.
        var taskOn = await FailedTaskWithOpenRow("on");
        var taskOff = await FailedTaskWithOpenRow("off");

        await Publish("on", taskOn);

        await UntilAsync(() => _runner.Calls.ToList(), c => c.Count > 0, "runner call for on");
        var (offIssues, offTriage) = await StoresFor("off");
        // Off-project's identically-id'd task and ledger are untouched.
        var offTask = await offIssues.GetAsync(taskOff);
        Assert.Equal(IssueStatus.Failed, offTask!.Status);
        Assert.Null(offTask.GetMetadata("triageAction"));
        var offRow = await offTriage.GetOpenForTaskAsync(taskOff);
        Assert.NotNull(offRow);
        Assert.Null(offRow!.Action);
        Assert.All(_runner.Calls, c => Assert.Equal("on", c.ProjectId));
    }
}
