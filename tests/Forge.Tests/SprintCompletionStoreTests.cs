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
    [Fact]
    public void RegistryMigrationDoesNotTouchWorkloadTables()
    {
        using var closed = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        new Forge.Core.Db.M041SprintCompletion().Up(closed, new Forge.Core.Db.SqlServerDialect("core"), Forge.Core.Db.ForgeSchemaProfile.Core);
        Assert.Equal(System.Data.ConnectionState.Closed, closed.State);
    }
    [Fact]
    public async Task ConsumptionFailureRollsBackCreatedTaskAndClaimThenReplaysOnce()
    {
        var (receipt, task) = await Complete();
        var batch = await Batch(receipt, task);
        var draft = System.Text.Json.JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)![0];
        await Store.TryBeginTriageAsync(batch.Id);
        await Store.SaveDecisionAsync(batch.Id, new([new("create", [draft.Id])]));
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_consumption BEFORE UPDATE OF consumed_at ON followup_draft BEGIN SELECT RAISE(ABORT,'injected consumption fault'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Store.MaterializeItemAsync(batch.Id, 0));
        Assert.Single(await issues.ListAsync(new IssueFilter()));
        Assert.Single(await new FollowUpDraftStore(issues).ListUnconsumedAsync());
        Assert.Equal("Decided", Assert.Single(await Store.ListBatchesAsync(receipt.Id)).State);
        command.CommandText = "DROP TRIGGER fail_consumption";
        await command.ExecuteNonQueryAsync();
        Assert.True((await Store.MaterializeItemWithResultAsync(batch.Id, 0)).Created);
        Assert.False((await Store.MaterializeItemWithResultAsync(batch.Id, 0)).Created);
        Assert.Equal(2, (await issues.ListAsync(new IssueFilter())).Count);
    }

    [Fact]
    public async Task LateDraftReopensBookkeepingDiscoveryAndCrossCompletionRetryIsRejected()
    {
        var (receipt, task) = await Complete();
        await Store.MarkBookkeepingCompleteAsync(receipt.Id);
        Assert.Empty(await Store.ListPendingBookkeepingAsync());
        var drafts = new FollowUpDraftStore(issues);
        await drafts.FileAsync(new(0, null, task.Id, "reviewer", "Late finding", "Details", 2, null, DateTime.UtcNow, null));
        Assert.Equal(receipt.Id, Assert.Single(await Store.ListPendingBookkeepingAsync()).Id);
        var batch = await Store.ClaimBatchAsync(receipt.Id, await Store.ListUnclaimedDraftsAsync(receipt.Id));
        Assert.Equal("Pending", (await Store.GetAsync(receipt.Id))!.BookkeepingState);
        await Store.TryBeginTriageAsync(batch.Id);
        await Store.RecoverRunningAsync();
        Assert.False(await Store.RetryTriageAsync(batch.Id, completionId: "different-completion"));
        Assert.Equal("NeedsReview", Assert.Single(await Store.ListBatchesAsync(receipt.Id)).State);
    }

    [Fact]
    public async Task MoreThanOneBatchCannotPrematurelyCompleteBookkeeping()
    {
        var (receipt, task) = await Complete();
        var drafts = new FollowUpDraftStore(issues);
        for (var i = 0; i < 101; i++)
            await drafts.FileAsync(new(0, receipt.SprintId, task.Id, "reviewer", "Finding " + i, "Detail", 2, null, DateTime.UtcNow, null));
        var first = await Store.ListUnclaimedDraftsAsync(receipt.Id);
        Assert.Equal(100, first.Count);
        var batch = await Store.ClaimBatchAsync(receipt.Id, first);
        await Store.TryBeginTriageAsync(batch.Id);
        await Store.SaveDecisionAsync(batch.Id, new([new("discard", first.Select(d => d.Id).ToArray(), Reason: "duplicate findings")]));
        await Store.MaterializeItemAsync(batch.Id, 0);
        await Store.MarkBookkeepingCompleteAsync(receipt.Id);
        Assert.Equal("Pending", (await Store.GetAsync(receipt.Id))!.BookkeepingState);
        Assert.Single(await Store.ListUnclaimedDraftsAsync(receipt.Id));
        Assert.Single(await Store.ListPendingBookkeepingAsync());
    }

    [Fact]
    public async Task LegacyProposalRecoveryCannotConsumeDurablyClaimedSources()
    {
        var (receipt, task) = await Complete();
        var batch = await Batch(receipt, task);
        var draft = System.Text.Json.JsonSerializer.Deserialize<FollowUpDraft[]>(batch.SourcesJson)![0];
        var drafts = new FollowUpDraftStore(issues);
        Assert.Empty(await drafts.ListUnconsumedAsync(excludeCompletionClaims: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => drafts.SetDispositionBatchAsync([draft.Id], "proposal", "proposal"));
        Assert.Single(await drafts.ListUnconsumedAsync());
        await Store.TryBeginTriageAsync(batch.Id);
        await Store.SaveDecisionAsync(batch.Id, new([new("epic", [draft.Id])]));
        Assert.Equal("proposal", await Store.MaterializeItemAsync(batch.Id, 0, "proposal"));
        Assert.Empty(await drafts.ListUnconsumedAsync());
    }
    public void Dispose() => issues.Dispose();
}
