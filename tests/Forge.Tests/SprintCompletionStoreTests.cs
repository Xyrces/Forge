using Forge.Core;
using Forge.Core.Completion;
using Xunit;

namespace Forge.Tests;

public sealed class SprintCompletionStoreTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("completion"), "project");
    private SprintCompletionStore Store => new(issues);
    private async Task<(SprintCompletionRecord Receipt, IssueRecord Task)> Complete()
    {
        var task = await issues.CreateAsync(new NewIssue("task", "Deliver"));
        var sprints = new SprintStore(issues);
        var sprint = await sprints.CreateAsync(new("Outcome", "Outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
        await sprints.AddIssueAsync(sprint.Id, task.Id);
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        task = (await issues.GetAsync(task.Id))!;
        return (await Store.BeginCompletionAsync(sprint.Id, [task], new(null, null, EngineeringOutcome.LegacyTerminalOnly, "{}")), task);
    }
    private async Task<CompletionTriageBatch> Batch(SprintCompletionRecord receipt, IssueRecord task)
    {
        var drafts = new FollowUpDraftStore(issues);
        var id = await drafts.FileAsync(new(0, receipt.SprintId, task.Id, "reviewer", "Finding", "Detail", 2, null, DateTime.UtcNow, null));
        return await Store.ClaimBatchAsync(receipt.Id, [(await drafts.GetAsync(id))!]);
    }
    [Fact]
    public async Task TerminalBookkeepingAndReceiptCommitTogetherAndReplay()
    {
        var (receipt, task) = await Complete();
        Assert.Equal(SprintStatus.Completed, (await new SprintStore(issues).GetAsync(receipt.SprintId))!.Status);
        Assert.Equal(receipt.Id, (await Store.BeginCompletionAsync(receipt.SprintId, [task], new(null, null, EngineeringOutcome.LegacyTerminalOnly, "{}"))).Id);
        Assert.False(receipt.ReadyForDeployment);
        Assert.Single(await Store.ListPendingBookkeepingAsync());
    }
    [Fact]
    public async Task DecisionAndAtomicMaterializationReplayWithoutDuplicateTasks()
    {
        var (receipt, task) = await Complete(); var batch = await Batch(receipt, task);
        Assert.True(await Store.TryBeginTriageAsync(batch.Id));
        var source = System.Text.Json.JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)!;
        var decision = new FollowUpTriageDecision([new("create", [source[0].Id])]);
        await Store.SaveDecisionAsync(batch.Id, decision);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Store.MaterializeItemAsync(batch.Id, 0)));
        Assert.Single(results.Distinct());
        Assert.Equal(2, (await issues.ListAsync(new IssueFilter())).Count);
        Assert.Empty(await new FollowUpDraftStore(issues).ListUnconsumedAsync());
        Assert.Equal("Completed", Assert.Single(await Store.ListBatchesAsync(receipt.Id)).State);
    }
    [Fact]
    public async Task InterruptedTriageRequiresExplicitRetryButRecordedDecisionSurvives()
    {
        var (receipt, task) = await Complete(); var batch = await Batch(receipt, task);
        Assert.True(await Store.TryBeginTriageAsync(batch.Id));
        await Store.RecoverRunningAsync();
        Assert.Equal("NeedsReview", Assert.Single(await Store.ListBatchesAsync(receipt.Id)).State);
        Assert.False(await Store.TryBeginTriageAsync(batch.Id));
        Assert.True(await Store.RetryTriageAsync(batch.Id));
        Assert.True(await Store.TryBeginTriageAsync(batch.Id));
        var source = System.Text.Json.JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)!;
        await Store.SaveDecisionAsync(batch.Id, new([new("discard", [source[0].Id], Reason: "duplicate finding")]));
        await Store.RecoverRunningAsync();
        Assert.Equal("Decided", Assert.Single(await Store.ListBatchesAsync(receipt.Id)).State);
    }
    [Fact]
    public async Task ImmutableSourceAndDeploymentBindingsRejectRetargeting()
    {
        var (receipt, _) = await Complete();
        await Store.RecordSourceAsync(receipt.Id, new string('a', 40), "{\"evidence\":1}");
        await Store.RecordSourceAsync(receipt.Id, new string('a', 40), "{\"evidence\":1}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecordSourceAsync(receipt.Id, new string('b', 40), "{}"));
        await Store.BindDeploymentAsync(receipt.Id, "candidate-one");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.BindDeploymentAsync(receipt.Id, "candidate-two"));
    }
    public void Dispose() => issues.Dispose();
}
