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
        var result = await WorkerHost.RunPhasesAsync(new Runner(_ => Task.FromResult(Result)), Invocation,
            new Dictionary<string, object> { ["dispatchEligibleAt"] = eligible.ToString("O"), ["workerStartedAt"] = started.ToString("O") },
            null, _ => Task.FromResult(Receipt), default);
        Assert.Equal(eligible, result.Phases!.EligibleAt);
        Assert.Equal(started, result.Phases.WorkerStartedAt);
        Assert.Equal(TimeSpan.FromSeconds(27), result.Phases.WorkerStartedAt - result.Phases.EligibleAt);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerHost.RunPhasesAsync(
            new Runner(_ => Task.FromResult(Result)), Invocation, new Dictionary<string, object>(), null,
            _ => throw new InvalidOperationException("verification failed"), default,
            (progress, _) => { recorded.Add(progress); return Task.CompletedTask; }));
        var model = Assert.Single(recorded);
        Assert.Null(model.Verification);
        Assert.NotNull(model.Phases);
        Assert.NotNull(model.Phases.VerificationStartedAt);
        Assert.Null(model.Phases.VerificationCompletedAt);
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

    private sealed class Runner(Func<CancellationToken, Task<AgentRunResult>> run) : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct) => run(ct);
    }
}
