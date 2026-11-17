using Forge.Core;
using Forge.Core.Initiatives;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Forge.Tests;

public sealed class InitiativeSourceConsumptionTests
{
    private static async Task<long> Draft(FollowUpDraftStore store, string title) => await store.FileAsync(
        new(0, null, "task-1", "reviewer", title, "detail", 2, null, DateTime.UtcNow, null));

    [Fact]
    public async Task SourceBatchIsAtomicAndSameBindingReplayPreservesTimestamp()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("source-batch"), "project");
        var store = new FollowUpDraftStore(issues);
        var a = await Draft(store, "one");
        var b = await Draft(store, "two");
        await using (var c = await issues.Db.OpenAsync())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE TRIGGER fail_second BEFORE UPDATE ON followup_draft WHEN NEW.id={b} BEGIN SELECT RAISE(ABORT,'fixture failure'); END";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.SetDispositionBatchAsync(new[] { a, b }, "proposal", "proposal-1"));
        Assert.Null((await store.GetAsync(a))!.ConsumedAt);
        Assert.Null((await store.GetAsync(b))!.ConsumedAt);
        await using (var c = await issues.Db.OpenAsync())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TRIGGER fail_second";
            await cmd.ExecuteNonQueryAsync();
        }
        await store.SetDispositionBatchAsync(new[] { a, b }, "proposal", "proposal-1");
        var original = await store.GetAsync(a);
        await store.SetDispositionBatchAsync(new[] { b, a }, "proposal", "proposal-1");
        Assert.Equal(original, await store.GetAsync(a));
        var cId = await Draft(store, "third");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetDispositionBatchAsync(new[] { cId, a }, "proposal", "different"));
        Assert.Null((await store.GetAsync(cId))!.ConsumedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetDispositionBatchAsync(new[] { cId, long.MaxValue }, "proposal", "proposal-2"));
        Assert.Null((await store.GetAsync(cId))!.ConsumedAt);
    }

    [Fact]
    public async Task RecoverCommittedProposalBeforeAnyDispositionWithoutRetriagingSources()
    {
        await using var primary = new IssueStore(TempRoot.Instance.NewDbPath("source-primary"), "primary");
        await using var target = new IssueStore(TempRoot.Instance.NewDbPath("source-target"), "project");
        var drafts = new FollowUpDraftStore(target);
        var a = await Draft(drafts, "one");
        var b = await Draft(drafts, "two");
        var proposals = new InitiativeProposalStore(primary);
        var proposal = await proposals.CreateAsync(new("project", "group:1:2", "forge", "Suggestion", "Not yet approved", SourceJson: System.Text.Json.JsonSerializer.Serialize(new { sourceDraftIds = new[] { a, b } })));
        // Crash before source acknowledgement: locate persisted binding from surviving unconsumed IDs.
        var bindings = await proposals.FindBySourceDraftIdsAsync("project", new[] { a });
        Assert.Equal(proposal.Id, Assert.Single(bindings).Id);
        Assert.Empty(await proposals.FindBySourceDraftIdsAsync("other", new[] { a, b }));
        await drafts.SetDispositionBatchAsync(new[] { a, b }, "proposal", proposal.Id);
        Assert.Empty(await drafts.ListUnconsumedAsync());
        Assert.Empty(await target.ListAsync(new IssueFilter()));
    }
}
