using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Initiatives;
using Forge.Core.Messaging;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Forge.Tests;

public sealed class InitiativeStoreTests
{
    private static IssueStore Store(string project = "project", IEventPublisher? publisher = null) => new(TempRoot.Instance.NewDbPath("initiative"), project, publisher);
    private static NewInitiativeProposal Draft(string project = "project", string source = "finding:1") => new(project, source, "forge", "A useful initiative", "Detailed proposed scope", 2, "{\"drafts\":[1]}");
    private static async Task<InitiativeProposal> Approved(InitiativeProposalStore store, string project = "project", string source = "finding:1")
    {
        var draft = await store.CreateAsync(Draft(project, source));
        await store.ExpandInIntakeAsync(project, draft.Id);
        return await store.ApproveAsync(project, draft.Id, draft.Revision, draft.Hash, "operator via private dashboard (unauthenticated)");
    }
    [Fact]
    public async Task SourceAndExpansionAreConcurrentIdempotentAndNotExecutableWork()
    {
        await using var issues = Store();
        var store = new InitiativeProposalStore(issues);
        var proposals = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CreateAsync(Draft())));
        Assert.Single(proposals.Select(p => p.Id).Distinct());
        var id = proposals[0].Id;
        var sessions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ExpandInIntakeAsync("project", id)));
        Assert.Single(sessions.Distinct());
        Assert.Single(await new IntakeStore(issues).ListAsync());
        Assert.Empty(await issues.ListAsync(new IssueFilter()));
        Assert.Empty(await new SpecStore(issues).ListAsync(null, null));
        Assert.Null(await store.GetAsync("other", id));
    }
    [Fact]
    public async Task ApprovalBindsLatestImmutableRevisionAndPreservesFirstOperator()
    {
        await using var issues = Store();
        var store = new InitiativeProposalStore(issues);
        var first = await store.CreateAsync(Draft());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApproveAsync("project", first.Id, 1, first.Hash, "operator"));
        await store.ExpandInIntakeAsync("project", first.Id);
        var revised = await store.ReviseAsync("project", first.Id, 1, "Revised initiative", "Revised scope", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApproveAsync("project", first.Id, 1, first.Hash, "operator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApproveAsync("project", first.Id, 2, first.Hash, "operator"));
        var approved = await store.ApproveAsync("project", first.Id, 2, revised.Hash, "operator-one");
        var repeat = await store.ApproveAsync("project", first.Id, 2, revised.Hash, "operator-two");
        Assert.Equal(approved, repeat);
        Assert.Equal("operator-one", repeat.ApprovedBy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviseAsync("project", first.Id, 2, "Another", "Different", 2));
        await using var c = await issues.Db.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT body FROM initiative_revision WHERE revision=1";
        Assert.Equal(first.Body, await cmd.ExecuteScalarAsync());
    }
    [Fact]
    public async Task SessionCannotRebindOrCrossProject()
    {
        await using var issues = Store();
        var store = new InitiativeProposalStore(issues);
        var proposal = await store.CreateAsync(Draft());
        var intake = new IntakeStore(issues);
        var other = await intake.CreateAsync("other", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LinkSessionAsync("project", proposal.Id, other.Id));
        var a = await intake.CreateAsync("project", null);
        var b = await intake.CreateAsync("project", null);
        Assert.Equal(a.Id, await store.LinkSessionAsync("project", proposal.Id, a.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LinkSessionAsync("project", proposal.Id, b.Id));
    }
    [Fact]
    public async Task ConcurrentIngestionCommitsOneEpicSpecAuditAndRecoversMissingPrimaryAcknowledgement()
    {
        var events = new Recorder();
        await using var primary = Store("primary");
        await using var target = Store("project", events);
        var proposals = new InitiativeProposalStore(primary);
        var approved = await Approved(proposals);
        var ingest = new InitiativeIngestionStore(target);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ingest.IngestAsync(approved)));
        Assert.Single(receipts.Select(r => r.EpicId).Distinct());
        Assert.Single(await target.ListAsync(new IssueFilter()));
        Assert.Single(await new SpecStore(target).ListAsync(null, null));
        Assert.Single(events.Events.OfType<TaskEnqueued>());
        var receipt = receipts[0];
        Assert.Contains(await target.ListEventsAsync(receipt.EpicId), e => e.Kind == "created");
        Assert.Equal(SpecStatus.Draft, (await new SpecStore(target).GetAsync(receipt.SpecId))!.Status);
        Assert.Single(await proposals.ListApprovedAsync("project")); // crash before primary acknowledgement
        Assert.Equal(receipt, await ingest.IngestAsync(approved));
        await proposals.MarkIngestedAsync("project", approved.Id, approved.Revision, receipt.EpicId, receipt.SpecId);
        Assert.Empty(await proposals.ListApprovedAsync("project"));
        Assert.Single(await ingest.ListPendingRefinementAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InitiativeIngestionStore(primary).IngestAsync(approved));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ingest.IngestAsync(approved with { Body = "tampered" }));
    }
    [Fact]
    public async Task FailureDuringSpecCreationRollsBackEpicEventAndReceipt()
    {
        var events = new Recorder();
        await using var issues = Store("project", events);
        var approved = await Approved(new InitiativeProposalStore(issues));
        await using (var c = await issues.Db.OpenAsync())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_spec BEFORE INSERT ON spec_version BEGIN SELECT RAISE(ABORT,'fixture crash boundary'); END";
            await cmd.ExecuteNonQueryAsync();
        }
        var ingest = new InitiativeIngestionStore(issues);
        await Assert.ThrowsAsync<SqliteException>(() => ingest.IngestAsync(approved));
        Assert.Empty(await issues.ListAsync(new IssueFilter()));
        Assert.Empty(await new SpecStore(issues).ListAsync(null, null));
        Assert.Null(await ingest.GetAsync(approved.Id, approved.Revision));
        Assert.Empty(events.Events);
        await using (var c = await issues.Db.OpenAsync())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TRIGGER fail_spec";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.NotNull(await ingest.IngestAsync(approved));
    }
    [Fact]
    public async Task RefinementClaimIsUniqueAndInterruptedPaidWorkRequiresExplicitRetry()
    {
        var events = new Recorder();
        await using var issues = Store("project", events);
        var approved = await Approved(new InitiativeProposalStore(issues));
        var ingest = new InitiativeIngestionStore(issues);
        var receipt = await ingest.IngestAsync(approved);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ingest.TryBeginRefinementAsync(approved.Id, 1)));
        Assert.Single(claims, x => x);
        Assert.Equal(1, await ingest.RecoverRunningAsync());
        Assert.Empty(await ingest.ListPendingRefinementAsync());
        Assert.False(await ingest.CompleteRefinementAsync(approved.Id, 1));
        Assert.True(await ingest.RetryRefinementAsync(approved.Id, 1));
        Assert.True(await ingest.TryBeginRefinementAsync(approved.Id, 1));
        await ingest.RecordRefinementFailureAsync(approved.Id, 1, "model unavailable");
        Assert.Empty(await ingest.ListPendingRefinementAsync());
        Assert.True(await ingest.RetryRefinementAsync(approved.Id, 1));
        Assert.True(await ingest.TryBeginRefinementAsync(approved.Id, 1));
        Assert.True(await ingest.CompleteRefinementAsync(approved.Id, 1));
        Assert.True(await ingest.CompleteRefinementAsync(approved.Id, 1));
        Assert.Equal(SpecStatus.ReadyForDesign, (await new SpecStore(issues).GetAsync(receipt.SpecId))!.Status);
        Assert.Equal("Completed", (await ingest.GetAsync(approved.Id, 1))!.RefinementState);
        Assert.Single(events.Events.OfType<SpecStatusChanged>());
    }
    [Fact]
    public async Task M040IsIdempotentAndLeavesExistingWorkUntouched()
    {
        await using var issues = Store();
        var issue = await issues.CreateAsync(new NewIssue("task", "existing"));
        await using var c = await issues.Db.OpenAsync();
        M040InitiativeApproval.Create(c, SqliteDialect.Instance);
        M040InitiativeApproval.Create(c, SqliteDialect.Instance);
        Assert.Equal(issue.Title, (await issues.GetAsync(issue.Id))!.Title);
        Assert.Equal(44, IssueStore.CurrentSchemaVersion);
        Assert.Equal(44, SqlServerMigrations.ExpectedVersion);
    }
    [Fact]
    public async Task SummariesAreBoundedAndSessionFilteringPrecedesPagination()
    {
        await using var issues = Store();
        var store = new InitiativeProposalStore(issues);
        var session = await new IntakeStore(issues).CreateAsync("project", "Selected conversation");
        for (var i = 0; i < 23; i++)
            await store.CreateAsync(Draft(source: "finding:" + i) with { SessionId = i % 2 == 0 ? session.Id : null });
        var ids = new HashSet<string>();
        string? after = null;
        do
        {
            var page = await store.ListAsync("project", limit: 5, afterId: after, sessionId: session.Id);
            Assert.InRange(page.Count, 0, 5);
            foreach (var row in page)
            {
                Assert.True(ids.Add(row.Id));
                Assert.Equal(session.Id, row.SessionId);
            }
            after = page.Count == 5 ? page[^1].Id : null;
        } while (after is not null);
        Assert.Equal(12, ids.Count);
        Assert.DoesNotContain(typeof(InitiativeProposalSummary).GetProperties(), p => p.Name is "Body" or "SourceJson");
        Assert.NotNull(await store.GetAsync("project", ids.First()));
    }

    [Fact]
    public async Task SourceIdentityCannotChangeOriginProvenanceOrSession()
    {
        await using var issues = Store();
        var store = new InitiativeProposalStore(issues);
        await store.CreateAsync(Draft());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Draft() with { Origin = "user" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Draft() with { SourceJson = "{}" }));
        var session = await new IntakeStore(issues).CreateAsync("project", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(Draft() with { SessionId = session.Id }));
        var other = await store.CreateAsync(Draft("other"));
        Assert.Equal("other", other.ProjectId); // source identity is intentionally scoped to its project
    }

    private sealed class Recorder : IEventPublisher
    {
        public List<IForgeEvent> Events
        {
            get;
        } = [];
        public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : IForgeEvent
        {
            lock (Events)
                Events.Add(evt);
            return Task.CompletedTask;
        }
    }
}

