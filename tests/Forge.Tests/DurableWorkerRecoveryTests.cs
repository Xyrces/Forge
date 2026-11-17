using Forge.Agents;
using Forge.Core;
using Forge.Core.Execution;
using Forge.Dashboard;
using Forge.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class DurableWorkerRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveDurableWorkerSurvivesRestartAndStaleHeartbeatReaping(bool stale)
    {
        await using var issues = new IssueStore(Path.Combine(TempRoot.Instance.NewDirectory("durable-recovery"), "issues.db"));
        using var runs = new AgentRunStore(issues.Db);
        var workers = new WorkerAttemptStore(issues.Db);
        await runs.StartAsync("live", "task-1", "CoreDev", "model", projectId: "forge");
        await runs.StartAsync("orphan", "task-2", "CoreDev", "model", projectId: "forge");
        await workers.CreateOrGetAsync(new("attempt", "dataset", "key", "forge", "task-1", "CoreDev", "dispatch",
            new string('a', 40), "/work", "{}", "token"));
        await workers.ClaimAsync("attempt", "pod");
        var closed = await runs.FailZombieRunsAsync(stale ? DateTime.UtcNow.AddMinutes(1) : null, "dead", default,
            (run, ct) => workers.HasActiveAsync("dataset", run.ProjectId!, run.TaskId!, ct));
        Assert.Equal(new[] { "orphan" }, closed);
        Assert.Equal("running", (await runs.GetAsync("live"))!.Status);
        await runs.UpdateProgressAsync("live", 2, 1, 10);
        Assert.Equal("running", (await runs.GetAsync("live"))!.Status);
    }

    [Fact]
    public async Task VerificationWorkerProtectsClaimEvenAfterModelRunFinished()
    {
        await using var issues = new IssueStore(Path.Combine(TempRoot.Instance.NewDirectory("durable-verify"), "issues.db"));
        using var runs = new AgentRunStore(issues.Db);
        var task = await issues.CreateAsync(new NewIssue(Type: "task", Title: "verification"));
        await issues.ClaimAsync(task.Id, "coredev");
        await runs.StartAsync("model", task.Id, "CoreDev", "model");
        await runs.FinishAsync("model", "succeeded", 1, 1, 1, 1, null, null);
        var workers = new WorkerAttemptStore(issues.Db);
        await workers.CreateOrGetAsync(new("attempt", "dataset", "key", "forge", task.Id, "CoreDev", "dispatch",
            new string('a', 40), "/work", "{}", "token"));
        await workers.ClaimAsync("attempt", "pod");
        var reaped = await OrphanedClaimReaper.ReapAsync(issues, runs, DateTime.UtcNow.AddHours(1),
            hasDurableWorker: (id, ct) => workers.HasActiveAsync("dataset", "forge", id, ct));
        Assert.Empty(reaped);
        Assert.Equal(IssueStatus.InProgress, (await issues.GetAsync(task.Id))!.Status);
        Assert.Equal(0, (await issues.GetAsync(task.Id))!.RecoveryAttempts);
        await workers.CompleteAsync("attempt", "pod", "token", "{}", null);
        Assert.Single(await OrphanedClaimReaper.ReapAsync(issues, runs, DateTime.UtcNow.AddHours(1),
            hasDurableWorker: (id, ct) => workers.HasActiveAsync("dataset", "forge", id, ct)));
    }

    [Fact]
    public async Task WorkerLookupFailureDoesNotAuthorizeReaping()
    {
        await using var issues = new IssueStore(Path.Combine(TempRoot.Instance.NewDirectory("durable-uncertain"), "issues.db"));
        using var runs = new AgentRunStore(issues.Db);
        await runs.StartAsync("live", "task", "CoreDev", "model");
        await Assert.ThrowsAsync<IOException>(() => runs.FailZombieRunsAsync(null, "dead", default,
            (_, _) => throw new IOException("registry unavailable")));
        Assert.Equal("running", (await runs.GetAsync("live"))!.Status);
    }
}

public class WatchBackgroundShutdownTests
{
    private static WatchSweepService Create() => new(new NeverRunner(), null, null,
        new ModelRateLimitTracker(), null, null, new InMemoryDashboardEventBus(),
        NullLoggerFactory.Instance, NullLogger<WatchSweepService>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsAndJoinsEntireCallbackBeforeReturning(bool qa)
    {
        var sweeps = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        Assert.True(sweeps.StartBackground("forge/task", qa, async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); }
            // Simulate bounded cleanup / a continuation that must finish before resources are disposed.
            await release.Task;
            finished = true;
        }, TimeSpan.FromMinutes(5)));
        await entered.Task;
        var stop = sweeps.StopAsync();
        await cancelled.Task;
        Assert.False(stop.IsCompleted);
        Assert.False(sweeps.StartBackground("late", false, _ => Task.CompletedTask, TimeSpan.FromMinutes(1)));
        release.SetResult();
        await stop;
        Assert.True(finished);
    }

    [Fact]
    public async Task ShutdownJoinsFollowOnWorkAndFaultsDoNotStrandDrain()
    {
        var sweeps = Create();
        var nested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(sweeps.StartBackground("qa", true, _ =>
        {
            Assert.True(sweeps.StartBackground("review", false, async ct =>
            {
                nested.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, TimeSpan.FromMinutes(1)));
            throw new IOException("QA callback failure");
        }, TimeSpan.FromMinutes(1)));
        await nested.Task;
        await sweeps.StopAsync();
        Assert.False(sweeps.StartBackground("late", true, _ => Task.CompletedTask, TimeSpan.FromMinutes(1)));
    }

    private sealed class NeverRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct) => throw new InvalidOperationException();
    }
}
