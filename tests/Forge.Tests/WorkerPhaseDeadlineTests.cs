using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator.Workers;
using Xunit;

namespace Forge.Tests;

public sealed class WorkerPhaseDeadlineTests
{
    private static readonly WorkerInvocation Invocation = new(AgentType.CoreDev, "task", null, []);
    private static readonly VerificationReceipt Receipt = new("attempt", "tree", "commands", DateTimeOffset.UtcNow);
    private static AgentRunResult Result => new("done", null, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EligibilityLatencyUsesRecordedAdmissionAndClaimNotTaskAge()
    {
        var eligible = DateTimeOffset.UtcNow.AddMinutes(-3);
        var started = eligible.AddSeconds(27);
        var wakeup = new DispatchWakeupTiming("rework", 123, "transition", "Pending", eligible.AddSeconds(-4), eligible);
        var result = await WorkerHost.RunPhasesAsync(new Runner(_ => Task.FromResult(Result)), Invocation,
            new Dictionary<string, object> { ["dispatchEligibleAt"] = eligible.ToString("O"), ["workerStartedAt"] = started.ToString("O"),
                ["dispatchWakeup"] = System.Text.Json.JsonSerializer.Serialize(wakeup) },
            null, _ => Task.FromResult(Receipt), default);
        Assert.Equal(eligible, result.Phases!.EligibleAt);
        Assert.Equal(started, result.Phases.WorkerStartedAt);
        Assert.Equal(TimeSpan.FromSeconds(27), result.Phases.WorkerStartedAt - result.Phases.EligibleAt);
        Assert.Equal(wakeup, result.Phases.QueueWakeup);
    }

    [Fact]
    public async Task ModelDeadlineStopsModelAndCannotPublishOrRunVerification()
    {
        var verified = false;
        var runner = new Runner(async ct => { await Task.Delay(Timeout.Infinite, ct); return Result; });
        await Assert.ThrowsAsync<TimeoutException>(() => WorkerHost.RunPhasesAsync(runner, Invocation,
            new Dictionary<string, object>(), TimeSpan.FromMilliseconds(30), _ =>
            { verified = true; return Task.FromResult(Receipt); }, default));
        Assert.False(verified);
    }

    [Fact]
    public async Task RunnerReturningPartialResultAfterDeadlineCannotProceedToVerification()
    {
        var verified = false;
        var runner = new Runner(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { return Result; }
            return Result;
        });
        await Assert.ThrowsAsync<TimeoutException>(() => WorkerHost.RunPhasesAsync(runner, Invocation,
            new Dictionary<string, object>(), TimeSpan.FromMilliseconds(30), _ =>
            { verified = true; return Task.FromResult(Receipt); }, default));
        Assert.False(verified);
    }

    [Fact]
    public async Task CompletedModelDeadlineDoesNotCancelIndependentVerification()
    {
        var calls = 0;
        var result = await WorkerHost.RunPhasesAsync(new Runner(_ => Task.FromResult(Result)), Invocation,
            new Dictionary<string, object>(), TimeSpan.FromMilliseconds(30), async ct =>
            {
                calls++;
                await Task.Delay(100, ct); // Deliberately exceeds the model phase's deadline.
                return Receipt;
            }, default);
        Assert.Equal(1, calls);
        Assert.Equal(Receipt, result.Verification);
        Assert.Equal("succeeded", result.Phases!.ModelOutcome);
        Assert.Equal("succeeded", result.Phases.VerificationOutcome);
        Assert.NotNull(result.Phases);
        Assert.True(result.Phases.ModelCompletedAt >= result.Phases.ModelStartedAt);
        Assert.True(result.Phases.VerificationCompletedAt >= result.Phases.VerificationStartedAt);
    }

    [Fact]
    public async Task OwnershipCancellationDuringVerificationStillPreventsPublication()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerHost.RunPhasesAsync(
            new Runner(_ => Task.FromResult(Result)), Invocation, new Dictionary<string, object>(),
            TimeSpan.FromSeconds(1), async ct =>
            {
                stop.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return Receipt;
            }, stop.Token));
    }

    [Fact]
    public async Task IndependentVerificationFailureCannotReturnModelSuccess()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerHost.RunPhasesAsync(
            new Runner(_ => Task.FromResult(Result)), Invocation, new Dictionary<string, object>(), null,
            _ => throw new InvalidOperationException("full suite failed"), default));
    }

    [Fact]
    public async Task VerificationFailureRetainsMeasuredModelProgressWithoutVerificationSuccess()
    {
        var recorded = new List<AgentRunResult>();
        AgentRunResult? failed = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerHost.RunPhasesAsync(
            new Runner(_ => Task.FromResult(Result)), Invocation, new Dictionary<string, object>(), null,
            _ => throw new InvalidOperationException("verification failed"), default,
            (progress, _) => { recorded.Add(progress); return Task.CompletedTask; }, failure => failed = failure));
        Assert.Equal(3, recorded.Count);
        Assert.Null(recorded[0].Phases!.ModelCompletedAt);
        Assert.Null(recorded[1].Phases!.VerificationStartedAt);
        Assert.Null(recorded[2].Phases!.VerificationCompletedAt);
        Assert.NotNull(failed);
        Assert.Null(failed.Verification);
        Assert.Equal("succeeded", failed.Phases!.ModelOutcome);
        Assert.Equal("failed", failed.Phases.VerificationOutcome);
        Assert.True(failed.Phases.VerificationCompletedAt >= failed.Phases.VerificationStartedAt);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("timed-out")]
    public async Task ModelStartIsRecordedBeforeWorkAndSurvivingFailureHasMeasuredEnd(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var recorded = new List<AgentRunResult>();
        AgentRunResult? failed = null;
        var runner = new Runner(async token =>
        {
            var start = Assert.Single(recorded).Phases!;
            Assert.NotNull(start.ModelStartedAt);
            Assert.Null(start.ModelCompletedAt);
            Assert.Equal("running", start.ModelOutcome);
            Assert.Null(start.VerificationStartedAt);
            if (outcome == "timed-out") await Task.Delay(Timeout.Infinite, token);
            if (outcome == "cancelled") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("model failure");
        });
        var error = await Record.ExceptionAsync(() => WorkerHost.RunPhasesAsync(runner, Invocation,
            new Dictionary<string, object>(), outcome == "timed-out" ? TimeSpan.FromMilliseconds(30) : null,
            _ => throw new Exception("Verification must not run"), cancellation.Token,
            (progress, _) => { recorded.Add(progress); return Task.CompletedTask; }, failure => failed = failure));
        Assert.NotNull(error);
        Assert.NotNull(failed);
        Assert.Equal(outcome, failed.Phases!.ModelOutcome);
        Assert.True(failed.Phases.ModelCompletedAt >= failed.Phases.ModelStartedAt);
        Assert.Null(failed.Phases.VerificationStartedAt);
        Assert.Null(failed.Verification);
    }

    [Fact]
    public async Task CancelledVerificationFinalizesEvidenceThroughExistingOwnerFence()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("cancelled-phase"));
        var store = new Forge.Core.Execution.WorkerAttemptStore(issues.Db);
        await store.CreateOrGetAsync(new("attempt", "dataset", "key", "forge", "task", "CoreDev", "dispatch",
            new string('a', 40), "/work", "{}", "token"));
        await store.ClaimAsync("attempt", "pod");
        using var cancellation = new CancellationTokenSource();
        AgentRunResult? failed = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerHost.RunPhasesAsync(
            new Runner(_ => Task.FromResult(Result)), Invocation, new Dictionary<string, object>(), null,
            async token => { await store.RequestCancellationAsync("attempt"); cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Receipt; },
            cancellation.Token, async (progress, token) => Assert.True(await store.RecordProgressAsync("attempt", "pod", "token", System.Text.Json.JsonSerializer.Serialize(progress), token)),
            failure => failed = failure));
        Assert.NotNull(failed);
        Assert.Equal("cancelled", failed.Phases!.VerificationOutcome);
        Assert.True(failed.Phases.VerificationCompletedAt >= failed.Phases.VerificationStartedAt);
        Assert.Null(failed.Verification);
        var payload = System.Text.Json.JsonSerializer.Serialize(failed);
        Assert.False(await store.CompleteAsync("attempt", "foreign", "token", payload, "cancelled"));
        Assert.True(await store.CompleteAsync("attempt", "pod", "token", payload, "cancelled"));
        Assert.Equal("cancelled", (await store.GetAsync("attempt"))!.State);
        var summary = Assert.Single(await store.ListSummariesAsync("dataset", "forge", "task"));
        Assert.Equal("cancelled", summary.Phases!.VerificationOutcome);
        Assert.False(summary.VerificationRecorded);
    }

    [Fact]
    public void LegacyPhasePayloadRemainsReadableWithoutInventingOutcome()
    {
        var phases = System.Text.Json.JsonSerializer.Deserialize<Forge.Core.Execution.WorkerPhaseTiming>(
            """{"ModelStartedAt":"2026-11-17T01:00:00Z","ModelCompletedAt":"2026-11-17T01:01:00Z"}""");
        Assert.NotNull(phases!.ModelStartedAt);
        Assert.NotNull(phases.ModelCompletedAt);
        Assert.Null(phases.ModelOutcome);
        Assert.Null(phases.WorkerCompletedAt);
    }

    [Fact]
    public async Task FailedWorkerPreservesProgressAndRejectsLateOrForeignPhaseWrites()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("worker-phases"));
        var store = new Forge.Core.Execution.WorkerAttemptStore(issues.Db);
        await store.CreateOrGetAsync(new("attempt", "dataset", "key", "forge", "task", "CoreDev", "dispatch",
            new string('a', 40), "/work", "{}", "token"));
        await store.ClaimAsync("attempt", "pod");
        Assert.False(await store.RecordProgressAsync("attempt", "other-pod", "token", "{}"));
        Assert.True(await store.RecordProgressAsync("attempt", "pod", "token", "{\"measured\":true}"));
        Assert.True(await store.CompleteAsync("attempt", "pod", "token", null, "verification failed"));
        var failed = (await store.GetAsync("attempt"))!;
        Assert.Equal("failed", failed.State);
        Assert.Equal("{\"measured\":true}", failed.ResultJson);
        Assert.False(await store.RecordProgressAsync("attempt", "pod", "token", "{}"));
    }

    [Fact]
    public async Task CancellationAfterFinalOwnershipCheckWinsOverSuccessfulPayload()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("late-cancel-phase"));
        var store = new Forge.Core.Execution.WorkerAttemptStore(issues.Db);
        await store.CreateOrGetAsync(new("attempt", "dataset", "key", "forge", "task", "CoreDev", "dispatch",
            new string('a', 40), "/work", "{}", "token"));
        await store.ClaimAsync("attempt", "pod");
        await store.EnsureOwnerAsync("attempt", "pod", "token", CancellationToken.None);
        await store.RequestCancellationAsync("attempt");
        var finished = DateTimeOffset.UtcNow;
        var result = Result with { Verification = Receipt, Phases = new(finished.AddMinutes(-1), finished,
            WorkerCompletedAt: finished, WorkerOutcome: "succeeded", ModelOutcome: "succeeded") };
        Assert.True(await store.CompleteAsync("attempt", "pod", "token", System.Text.Json.JsonSerializer.Serialize(result), null));
        var terminal = (await store.GetAsync("attempt"))!;
        Assert.Equal("cancelled", terminal.State);
        Assert.Equal("cancelled", terminal.Error);
        var stored = System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(terminal.ResultJson!)!;
        Assert.Equal("cancelled", stored.Phases!.WorkerOutcome);
        Assert.Equal("succeeded", stored.Phases.ModelOutcome);
        Assert.Equal(finished, stored.Phases.WorkerCompletedAt);
        Assert.Equal(Receipt, stored.Verification);
        Assert.False(await store.CompleteAsync("attempt", "pod", "token", null, null));
    }

    [Fact]
    public void InitialCheckpointRetainsKnownQueueProvenanceBeforeSetup()
    {
        var eligible = DateTimeOffset.UtcNow.AddSeconds(-12);
        var started = DateTimeOffset.UtcNow;
        var wake = new DispatchWakeupTiming("task-ready", 1, "task", "ready", eligible.AddSeconds(-8), eligible);
        var invocation = Invocation with { Context = new Dictionary<string, string>
        {
            ["dispatchEligibleAt"] = eligible.ToString("O"),
            ["dispatchWakeup"] = System.Text.Json.JsonSerializer.Serialize(wake)
        } };
        var initial = WorkerHost.InitialProgress(invocation, started);
        Assert.Equal(eligible, initial.Phases!.EligibleAt);
        Assert.Equal(wake, initial.Phases.QueueWakeup);
        Assert.Equal(started, initial.Phases.WorkerStartedAt);
        Assert.Null(initial.Phases.ModelStartedAt);
        Assert.Null(initial.Phases.VerificationStartedAt);
    }

    [Theory]
    [InlineData("historical text")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task CancellationRetainsHistoricalUntypedResults(string payload)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("opaque-cancel"));
        var store = new Forge.Core.Execution.WorkerAttemptStore(issues.Db);
        await store.CreateOrGetAsync(new("attempt", "dataset", "key", "forge", "task", "CoreDev", "dispatch",
            new string('a', 40), "/work", "{}", "token"));
        await store.ClaimAsync("attempt", "pod");
        await store.RequestCancellationAsync("attempt");
        Assert.True(await store.CompleteAsync("attempt", "pod", "token", payload, null));
        var terminal = (await store.GetAsync("attempt"))!;
        Assert.Equal("cancelled", terminal.State);
        Assert.Equal(payload, terminal.ResultJson);
    }

    private sealed class Runner(Func<CancellationToken, Task<AgentRunResult>> run) : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct) => run(ct);
    }
}
