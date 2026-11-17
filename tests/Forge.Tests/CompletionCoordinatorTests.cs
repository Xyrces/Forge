using Forge.Core;
using Forge.Core.Completion;
using Forge.Orchestrator.Sprint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class CompletionCoordinatorTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("completion-resume"), "project");

    [Fact]
    public async Task UncertainPaidTriageParksAcrossTicksAndRestartUntilExplicitRetry()
    {
        var task = await issues.CreateAsync(new NewIssue("task", "Done"));
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("Sprint", "Sprint", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        var drafts = new FollowUpDraftStore(issues);
        for (var i = 0; i < 2; i++)
            await drafts.FileAsync(new(0, sprint.Id, task.Id, "reviewer", "Finding " + i, "Detail", 2, null, DateTime.UtcNow, null));
        var triage = new ThrowingTriage();
        CompletionCoordinator Coordinator() => new(issues, triage, null, NullLogger.Instance);
        var coordinator = Coordinator();
        var receipt = await coordinator.CompleteAsync(sprint, sprints, default);
        await coordinator.ReconcileAsync(default);
        await coordinator.ReconcileAsync(default);
        await Coordinator().ReconcileAsync(default);
        Assert.Equal(1, triage.Calls);
        var store = new SprintCompletionStore(issues);
        var batch = Assert.Single(await store.ListBatchesAsync(receipt.Id));
        Assert.Equal("NeedsReview", batch.State);
        Assert.Empty(await store.ListPlanningBlockersAsync()); // uncertain optional follow-ups do not hold unrelated approved scope
        Assert.Single(await store.ListOverviewAsync()); // the uncertainty remains visible
        Assert.Equal(2, (await drafts.ListUnconsumedAsync()).Count);
        Assert.True(await store.RetryTriageAsync(batch.Id));
        await coordinator.ReconcileAsync(default);
        Assert.Equal(2, triage.Calls);
    }

    private sealed class ThrowingTriage : IFollowUpTriage
    {
        public int Calls;
        public Task<FollowUpTriageDecision?> TriageAsync(string projectId, IReadOnlyList<FollowUpDraft> drafts, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Paid response outcome uncertain");
        }
    }
    public void Dispose() => issues.Dispose();
}
