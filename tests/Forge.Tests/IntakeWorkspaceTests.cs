using Forge.Core;
using Forge.Core.Initiatives;
using Xunit;

namespace Forge.Tests;

public sealed class IntakeWorkspaceTests
{
    [Fact]
    public async Task InboxGroupsRealRecordsAndPaginatesWithoutLoadingTranscripts()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("intake-inbox"));
        var store = new InitiativeProposalStore(issues);
        var sessions = new IntakeStore(issues);
        var open = await sessions.CreateAsync("forge", "New session");
        await sessions.AppendMessageAsync(open.Id, new(IntakeMessageRole.User, "Show me the cost of each benchmark."));
        var linked = await sessions.CreateAsync("forge", "Baseline");
        var review = await store.CreateAsync(new("forge", "user:1", "user", "Baseline", "Original", SessionId: linked.Id));
        var sibling = await store.CreateAsync(new("forge", "user:2", "user", "Compare workers", "Scope", SessionId: linked.Id));
        var suggestion = await store.CreateAsync(new("forge", "suggest:1", "forge", "Investigate rework", "Why"));
        var approved = await store.CreateAsync(new("forge", "suggest:2", "forge", "Sprint goal", "Acceptance"));
        await store.ExpandInIntakeAsync("forge", approved.Id);
        await store.ApproveAsync("forge", approved.Id, approved.Revision, approved.Hash, "operator");
        var foreign = await sessions.CreateAsync("other", "Other project");
        await store.CreateAsync(new("other", "user:1", "user", "Private", "Private", SessionId: foreign.Id));

        var page = await store.ReadInboxAsync("forge", null, default, 2);
        Assert.Equal(new[] { review.Id, sibling.Id }.Order(), page.Items.Select(i => i.Id).Order());
        Assert.All(page.Items, i => Assert.Equal("review", i.Group));
        Assert.NotNull(page.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadInboxAsync("other", page.NextCursor, default));
        var second = await store.ReadInboxAsync("forge", page.NextCursor, default, 2);
        Assert.Collection(second.Items, item => { Assert.Equal("suggestion", item.Group); Assert.Equal(suggestion.Id, item.Id); },
            item => { Assert.Equal("discussion", item.Group); Assert.Equal(open.Id, item.SessionId); Assert.Contains("benchmark", item.Title); });
        var third = await store.ReadInboxAsync("forge", second.NextCursor, default, 2);
        var last = Assert.Single(third.Items);
        Assert.Equal(approved.Id, last.Id);
        Assert.Equal("planning", last.Group);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task RevisionReadsAreImmutableAndProjectScopedAndDoNotAuthorizeAnOlderDraft()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("intake-history"));
        var proposals = new InitiativeProposalStore(issues);
        var draft = await proposals.CreateAsync(new("forge", "example", "forge", "First title", "Original wording.", 2));
        await proposals.ExpandInIntakeAsync("forge", draft.Id);
        var latest = await proposals.ReviseAsync("forge", draft.Id, 1, "Latest title", "Updated wording.", 1);
        var prior = await proposals.ReadRevisionAsync("forge", draft.Id, 1, default);
        Assert.Equal(new InitiativeRevision(1, draft.Title, draft.Body, draft.Priority, draft.Hash), prior);
        Assert.Equal(latest.Hash, (await proposals.ReadRevisionAsync("forge", draft.Id, 2, default))!.Hash);
        Assert.Null(await proposals.ReadRevisionAsync("other", draft.Id, 1, default));
        Assert.Null(await proposals.ReadRevisionAsync("forge", draft.Id, 3, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposals.ApproveAsync("forge", draft.Id, 1, draft.Hash, "operator"));
        Assert.Null((await proposals.GetAsync("forge", draft.Id))!.ApprovedAt);
    }
}
