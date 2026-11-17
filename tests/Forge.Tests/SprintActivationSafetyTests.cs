using Forge.Agents;
using Forge.Core;
using Forge.Core.Db;
using System.Data.Common;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

public sealed class SprintActivationSafetyTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("sprint-safety"), "test");
    private SprintStore Sprints => new(issues);
    private SprintProposeService Proposer(StageGates? gates = null) => new(issues, Sprints,
        new DeterministicScorer(), new SprintProposalAuditStore(issues.Db), gates, projectId: "test");
    private SprintScopeReference? scope;
    private async Task<IssueRecord> TaskRow(bool groomed = true, string type = "task")
    {
        scope ??= await ApprovedSprintFixture.Scope(issues);
        var row = await issues.CreateAsync(new NewIssue(type,"test",Metadata:new Dictionary<string,object>{["groomed"]=groomed}));
        if (!AgentTaskTypes.IsContainer(type)) await ApprovedSprintFixture.Bind(issues,scope,[row]);
        return (await issues.GetAsync(row.Id))!;
    }
    private static NewSprint New() => new("test", "test", DateTime.UtcNow, DateTime.UtcNow.AddDays(7));
    public void Dispose() => issues.Dispose();

    [Fact]
    public async Task ProposalBindsAndCommitsEntirePrerequisiteClosure()
    {
        var outcome = await TaskRow();
        var prerequisite = await TaskRow();
        await issues.AddDependencyAsync(prerequisite.Id, outcome.Id, IssueDepKind.Blocks);
        await ApprovedSprintFixture.Bind(issues,scope!,[outcome,prerequisite]);
        var proposer = Proposer();
        var proposal = await proposer.ProposeAsync(null, null);
        Assert.Equal(2,proposal.SelectedTaskIds.Count);
        var sprint = await proposer.CommitAsync(proposal.AuditId, proposal.SelectedTaskIds, null, null, "operator");
        var members = await Sprints.GetIssueIdsAsync(sprint);
        Assert.Contains(outcome.Id, members);
        Assert.Contains(prerequisite.Id, members);
        Assert.True(await issues.IsBlockedAsync(outcome.Id));
    }

    [Theory]
    [InlineData("ungroomed")]
    [InlineData("failed")]
    [InlineData("blocked")]
    [InlineData("cycle")]
    public async Task InvalidPrerequisiteClosureIsNeverAnEligibleProposal(string reason)
    {
        var outcome = await TaskRow();
        var prerequisite = await TaskRow(reason != "ungroomed");
        await issues.AddDependencyAsync(prerequisite.Id, outcome.Id, IssueDepKind.Blocks);
        if (reason == "failed") await issues.TransitionAsync(prerequisite.Id, IssueStatus.Failed, null);
        if (reason == "blocked") await issues.TransitionAsync(prerequisite.Id, IssueStatus.Blocked, null);
        if (reason == "cycle") await issues.AddDependencyAsync(outcome.Id, prerequisite.Id, IssueDepKind.Blocks);
        var proposal = await Proposer().ProposeAsync(null, null);
        Assert.DoesNotContain(proposal.Candidates, c => c.TaskId == outcome.Id);
    }

    [Fact]
    public async Task ChangedSatisfiedPrerequisiteInvalidatesPreview()
    {
        var outcome = await TaskRow();
        var prerequisite = await TaskRow();
        await issues.TransitionAsync(prerequisite.Id, IssueStatus.Completed, null);
        await issues.AddDependencyAsync(prerequisite.Id, outcome.Id, IssueDepKind.Blocks);
        var proposer = Proposer();
        var proposal = await proposer.ProposeAsync(null, null);
        await issues.TransitionAsync(prerequisite.Id, IssueStatus.Pending, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            proposer.CommitAsync(proposal.AuditId, [outcome.Id], null, null, "operator"));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task LargeIneligibleBacklogUsesBoundedReadsAndStillFindsEligibleWork()
    {
        var db = new CountingFactory(issues.Db);
        await using var counted = new IssueStore(db);
        for (var i = 0; i < 120; i++)
            await counted.CreateAsync(new NewIssue("task", "ungroomed", Priority: 1));
        var eligible = await counted.CreateAsync(new NewIssue("task", "eligible", Priority: 5,
            Metadata: new Dictionary<string, object> { ["groomed"] = true }));
        var approved = await ApprovedSprintFixture.Scope(counted);
        await ApprovedSprintFixture.Bind(counted,approved,[eligible]);
        db.Opens = 0;
        var proposer = new SprintProposeService(counted, new SprintStore(counted), new DeterministicScorer(), new SprintProposalAuditStore(db));
        var firstPage = await proposer.ProposeAsync(null, null, 1);
        Assert.Empty(firstPage.Candidates);
        var proposal = await proposer.ProposeAsync(null, null, 1);
        Assert.Equal(eligible.Id, Assert.Single(proposal.Candidates).TaskId);
        Assert.InRange(db.Opens, 1, 35);
    }

    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int Opens;
        public bool InitializeSchema => false;
        public ForgeDbProvider Provider => inner.Provider;
        public ISqlDialect Dialect => inner.Dialect;
        public ForgeSchemaProfile Profile => inner.Profile;
        public string ConnectionString => inner.ConnectionString;
        public string Qualifier => inner.Qualifier;
        public DbConnection Open() { Opens++; return inner.Open(); }
        public Task<DbConnection> OpenAsync(CancellationToken ct = default) { Opens++; return inner.OpenAsync(ct); }
    }

    [Fact]
    public async Task ArbitraryIdsAndUngroomedContainersCannotBeCommitted()
    {
        var valid = await TaskRow();
        var ungroomed = await TaskRow(false);
        var story = await TaskRow(type: "story");
        var proposer = Proposer();
        var proposal = await proposer.ProposeAsync(null, null);
        Assert.Equal(valid.Id, Assert.Single(proposal.Candidates).TaskId);
        var later = await TaskRow();
        foreach (var id in new[] { ungroomed.Id, story.Id, later.Id, "missing" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId, [id], null, null, "test"));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task HeldGateAfterProposalPreventsActivation()
    {
        var task = await TaskRow();
        var gates = new StageGates(new MemoryStore(issues.Db));
        var proposer = Proposer(gates);
        var proposal = await proposer.ProposeAsync(null, null);
        await gates.HoldAsync(StageGates.Sprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId, [task.Id], null, null, "test"));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task PreviewRemainsAvailableWhileHeldOrAnotherSprintIsActive()
    {
        var task = await TaskRow();
        var gates = new StageGates(new MemoryStore(issues.Db));
        await gates.HoldAsync(StageGates.Sprint);
        await Sprints.CreateAsync(New());
        var proposer = Proposer(gates);
        var proposal = await proposer.ProposeAsync(null, null);
        Assert.Equal(task.Id, Assert.Single(proposal.Candidates).TaskId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId, [task.Id], null, null, "test"));
        Assert.Single(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task ChangedTaskRejectsStaleProposal()
    {
        var task = await TaskRow();
        var proposer = Proposer();
        var proposal = await proposer.ProposeAsync(null, null);
        await issues.UpdateSummaryAsync(task.Id, "changed", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId, [task.Id], null, null, "test"));
        Assert.Null(await Sprints.GetActiveAsync());
    }

    [Fact]
    public async Task ForeignSpecIsNotAProposalCandidate()
    {
        var specs = new SpecStore(issues);
        var spec = await specs.CreateAsync(new NewSpec("other", "foreign", "body"));
        await specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        await specs.SetStatusAsync(spec.Id, SpecStatus.Grooming);
        await specs.SetStatusAsync(spec.Id, SpecStatus.Groomed);
        var story = await issues.CreateAsync(new NewIssue("story", "story", ParentId: spec.Id));
        await issues.CreateAsync(new NewIssue("task", "foreign task", ParentId: story.Id));
        Assert.Empty((await Proposer().ProposeAsync(null, null)).Candidates);
    }

    [Fact]
    public async Task SpecificationChangeInvalidatesProposalAndTransactionalSnapshot()
    {
        var specs = new SpecStore(issues);
        var approved = await ApprovedSprintFixture.Scope(issues);
        var spec = (await specs.GetAsync(approved.SpecId))!;
        await specs.SetStatusAsync(spec.Id, SpecStatus.Approved);
        await specs.SetStatusAsync(spec.Id, SpecStatus.Grooming);
        await specs.SetStatusAsync(spec.Id, SpecStatus.Groomed);
        var story = await issues.CreateAsync(new NewIssue("story", "story", ParentId: spec.Id));
        var task = await issues.CreateAsync(new NewIssue("task", "task", ParentId: story.Id));
        await ApprovedSprintFixture.Bind(issues,approved,[task]);
        var proposer = Proposer();
        var proposal = await proposer.ProposeAsync(null, null);
        var snapshot = await new SprintActivationValidator(issues, Sprints, specs, "test", null).ValidateAsync([task.Id], default);
        await specs.UpdateBodyAsync(spec.Id, new UpdateSpecBody("changed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId, [task.Id], null, null, "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sprints.CreateWithIssuesAsync(New(), snapshot));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task PrerequisiteAddedAfterValidationPreventsActivation()
    {
        var task = await TaskRow();
        var blocker = await TaskRow();
        var snapshot = await new SprintActivationValidator(issues, Sprints, new SpecStore(issues), "test", null).ValidateAsync([task.Id], default);
        await issues.AddDependencyAsync(blocker.Id, task.Id, IssueDepKind.Blocks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sprints.CreateWithIssuesAsync(New(), snapshot));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task FailedSnapshotRollsBackSprintAndAllMembership()
    {
        var task = await TaskRow();
        var snapshot = new SprintActivationSnapshot([task], [], [task.Id]);
        await issues.UpdateSummaryAsync(task.Id, "changed", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sprints.CreateWithIssuesAsync(New(), snapshot));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Theory]
    [InlineData("removed-edge")]
    [InlineData("removed-target")]
    [InlineData("completed-target")]
    public async Task AutomaticAmendmentRequiresCurrentUnfinishedPrerequisiteRelationship(string change)
    {
        var target = await TaskRow();
        var fixer = await TaskRow();
        var initial = await new SprintActivationValidator(issues,Sprints,new SpecStore(issues),"test",null).ValidateAsync([target.Id],default);
        var sprint = await Sprints.CreateWithIssuesAsync(New(),initial);
        await issues.AddDependencyAsync(fixer.Id, target.Id, IssueDepKind.Blocks);
        var snapshot = await new SprintActivationValidator(issues, Sprints, new SpecStore(issues), "test", null)
            .ValidateAsync([fixer.Id], default, activating: false);
        if (change == "removed-edge") await issues.RemoveDependencyAsync(fixer.Id, target.Id, IssueDepKind.Blocks);
        else if (change == "removed-target")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Sprints.RemoveIssueAsync(sprint.Id, target.Id));
            Assert.Contains(target.Id, await Sprints.GetIssueIdsAsync(sprint.Id));
            await Sprints.AddValidatedPrerequisiteAsync(sprint.Id, snapshot);
            Assert.Contains(fixer.Id, await Sprints.GetIssueIdsAsync(sprint.Id));
            return;
        }
        else await issues.TransitionAsync(target.Id, IssueStatus.Completed, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sprints.AddValidatedPrerequisiteAsync(sprint.Id, snapshot));
        Assert.DoesNotContain(fixer.Id, await Sprints.GetIssueIdsAsync(sprint.Id));
        // Explicit manual amendments must still preserve the approved criterion and member set.
        snapshot = await new SprintActivationValidator(issues, Sprints, new SpecStore(issues), "test", null)
            .ValidateAsync([fixer.Id], default, activating: false);
        await Sprints.AddValidatedIssueAsync(sprint.Id, snapshot);
        Assert.Contains(fixer.Id, await Sprints.GetIssueIdsAsync(sprint.Id));
    }

    [Fact]
    public async Task MembershipInsertFailureRollsBackWholeActivation()
    {
        var task = await TaskRow();
        // A duplicate display member fails after the first membership INSERT.
        // No observer may discover the uncommitted active sprint or partial membership.
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Sprints.CreateWithIssuesAsync(
            New(), new([task, task], [], [task.Id])));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task CompetingActivationsHaveExactlyOneCompleteWinner()
    {
        var first = await TaskRow();
        var second = await TaskRow();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        async Task<bool> Activate(IssueRecord issue)
        {
            // Dedicated callers exercise separate DB connections and the transaction boundary.
            return await Task.Factory.StartNew(() =>
            {
                ready.Signal(); release.Wait();
                try { Sprints.CreateWithIssuesAsync(New(), new([issue], [], [issue.Id])).GetAwaiter().GetResult(); return true; }
                catch (InvalidOperationException) { return false; }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return false; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        var a = Activate(first); var b = Activate(second);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10))); release.Set();
        var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x);
        var sprint = Assert.Single(await Sprints.ListAsync(false));
        Assert.Single(await Sprints.GetIssueIdsAsync(sprint.Id));
    }

    [Fact]
    public async Task SuccessfulCommitHasMembershipAndAuditTogetherAndCannotReplay()
    {
        var task = await TaskRow();
        var proposer = Proposer();
        var proposal = await proposer.ProposeAsync(null, null);
        var sprint = await proposer.CommitAsync(proposal.AuditId, [task.Id], null, null, "operator-test");
        Assert.Equal(task.Id, Assert.Single(await Sprints.GetIssueIdsAsync(sprint)));
        var audit = await new SprintProposalAuditStore(issues.Db).GetAsync(proposal.AuditId);
        Assert.Equal(sprint, audit!.CommittedSprintId);
        Assert.Equal("operator-test", audit.CommittedBy);
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        var plan = (await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(sprint))!;
        await new Forge.Core.Completion.SprintCompletionStore(issues).BeginCompletionAsync(sprint,
            [(await issues.GetAsync(task.Id))!], new(audit.Id, plan.Hash, Forge.Core.Completion.EngineeringOutcome.PendingEvidence, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId, [task.Id], null, null, "operator-test"));
        Assert.Single(await Sprints.ListAsync(false));
    }
}
