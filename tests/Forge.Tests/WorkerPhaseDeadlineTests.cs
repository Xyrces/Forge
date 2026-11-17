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

    private sealed class Runner(Func<CancellationToken, Task<AgentRunResult>> run) : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct) => run(ct);
    }
}
