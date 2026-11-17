using Forge.Core;
using Forge.Core.Completion;
using Forge.Core.Initiatives;
using Forge.Configuration;
using Forge.Orchestrator;
using Forge.Agents;
using Xunit;

namespace Forge.Tests;

public sealed class SprintOutcomePlannerTests
{
    private static IssueStore Store() => new(TempRoot.Instance.NewDbPath("outcomes"), "project");
    private static SprintPlanBuilder Planner(IssueStore issues, int cap = 10, int slots = 3) => new(issues,
        new SprintStore(issues), new SpecStore(issues), issues.ProjectId, null, new() { MaxEngineeringTasks = cap }, new Dictionary<string, int> { ["coredev"] = slots });

    [Fact]
    public async Task SmallCompleteOutcomeDoesNotPadToTenWithUnrelatedWork()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var a = await ApprovedSprintFixture.Task(issues); var b = await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues, scope, [a]); await ApprovedSprintFixture.Bind(issues, scope, [b], "unrelated", "other");
        var result = await Planner(issues).BuildAsync();
        Assert.Equal(a.Id, Assert.Single(result.Snapshot!.TaskIds));
        Assert.Contains(result.Snapshot.Plan!.Deferred, d => d.TaskId == b.Id);
        Assert.True(result.Snapshot.Plan.HasValidHash);
    }
    [Fact]
    public async Task NaturalIndependentUnitsUseThreeSlotsButSequentialUnitsDoNotCountAsParallel()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var tasks = new List<IssueRecord>();
        for (var i = 0; i < 4; i++) { var t = await ApprovedSprintFixture.Task(issues); tasks.Add(t); await ApprovedSprintFixture.Bind(issues, scope, [t], criterion: "c" + i); }
        var result = await Planner(issues, slots: 3).BuildAsync();
        Assert.Equal(3, result.Snapshot!.TaskIds.Count);
        Assert.Single(result.Snapshot.Plan!.Deferred);
        var oneSlot = await Planner(issues, slots: 1).BuildAsync();
        Assert.Single(oneSlot.Snapshot!.TaskIds);
    }
    [Fact]
    public async Task CompleteClosureAboveCapDefersRatherThanTruncating()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var tasks = new List<IssueRecord>();
        for (var i = 0; i < 11; i++) tasks.Add(await ApprovedSprintFixture.Task(issues));
        await ApprovedSprintFixture.Bind(issues, scope, tasks);
        var result = await Planner(issues).BuildAsync(); Assert.Null(result.Snapshot);
        Assert.Contains(result.Deferred, d => d.Reason.Contains("ceiling"));
        Assert.Empty(await new SprintStore(issues).ListAsync(false));
    }
    [Fact]
    public async Task ProposalRequiresExactCompleteSelectionAndCommitsTypedAuditAtomically()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var a = await ApprovedSprintFixture.Task(issues); var b = await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues, scope, [a,b]); await issues.AddDependencyAsync(a.Id,b.Id,IssueDepKind.Blocks);
        var audits = new SprintProposalAuditStore(issues.Db);
        var proposer = new SprintProposeService(issues, new SprintStore(issues), new DeterministicScorer(), audits);
        var result = await proposer.ProposeAsync(null,null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(result.AuditId,[a.Id],null,null,"operator"));
        var id = await proposer.CommitAsync(result.AuditId,result.SelectedTaskIds,null,null,"operator");
        var plan = await audits.GetPlanForSprintAsync(id); Assert.NotNull(plan); Assert.Equal(2,plan.Members.Count);
        Assert.Equal(plan.Objective,(await new SprintStore(issues).GetAsync(id))!.Goal);
    }
    [Fact]
    public async Task PendingOutcomeEvidencePreventsNewActivationAndCannotBeDeletedOrManuallyCompleted()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var first = await ApprovedSprintFixture.Task(issues); await ApprovedSprintFixture.Bind(issues, scope, [first]);
        var sprints = new SprintStore(issues); var audits = new SprintProposalAuditStore(issues.Db);
        var initial = (await Planner(issues).BuildAsync()).Snapshot!;
        var sprint = await sprints.CreateWithIssuesAsync(new("first", "first", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), initial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.UpdateAsync(sprint.Id, new Dictionary<string, object?> { ["status"] = "Completed" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.DeleteAsync(sprint.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.RemoveIssueAsync(sprint.Id, first.Id));
        Assert.Equal(SprintStatus.Active, (await sprints.GetAsync(sprint.Id))!.Status);
        await issues.TransitionAsync(first.Id, IssueStatus.Completed, null);
        var audit = Assert.Single(await audits.ListAsync());
        await new SprintCompletionStore(issues).BeginCompletionAsync(sprint.Id, [(await issues.GetAsync(first.Id))!],
            new(audit.Id, initial.Plan!.Hash, EngineeringOutcome.PendingEvidence, "{}"));
        var next = await ApprovedSprintFixture.Task(issues); await ApprovedSprintFixture.Bind(issues, scope, [next], "next", "next");
        var snapshot = (await Planner(issues).BuildAsync()).Snapshot!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.CreateWithIssuesAsync(new("next", "next", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot));
        Assert.Contains("completion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await sprints.ListAsync(false)); Assert.Single(await audits.ListAsync());
        var draft = await sprints.CreateAsync(new("draft", "draft", DateTime.UtcNow, DateTime.UtcNow.AddDays(1), SprintStatus.Archived));
        await sprints.AddInactiveIssueAsync(draft.Id, next.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.ActivateWithIssuesAsync(draft.Id, snapshot, [next.Id]));
        Assert.Equal(SprintStatus.Archived, (await sprints.GetAsync(draft.Id))!.Status);
        Assert.Single(await audits.ListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.DeleteAsync(sprint.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.RemoveIssueAsync(sprint.Id, first.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.AddInactiveIssueAsync(sprint.Id, next.Id));
        Assert.Equal(first.Id, Assert.Single(await sprints.GetIssueIdsAsync(sprint.Id)));
        Assert.Single(await new SprintCompletionStore(issues).ListPlanningBlockersAsync());
    }

    [Fact]
    public async Task ArbitraryGroomedTaskAndTamperedAuthorityRemainIneligible()
    {
        await using var issues = Store(); var task = await ApprovedSprintFixture.Task(issues);
        Assert.Null((await Planner(issues).BuildAsync()).Snapshot);
        var scope = await ApprovedSprintFixture.Scope(issues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovedSprintFixture.Bind(issues,scope with { Hash="forged" },[task]));
        Assert.Null((await Planner(issues).BuildAsync()).Snapshot);
    }
    [Fact]
    public async Task StaleApprovalContentRollsBackActivationAndItsAudit()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues); await ApprovedSprintFixture.Bind(issues,scope,[task]);
        var snapshot = (await Planner(issues).BuildAsync()).Snapshot!;
        var receipt = await new InitiativeIngestionStore(issues).GetBySpecAsync(scope.SpecId);
        await issues.SetPriorityAsync(receipt!.EpicId,1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SprintStore(issues).CreateWithIssuesAsync(new("x","x",DateTime.UtcNow,DateTime.UtcNow.AddDays(1)),snapshot));
        Assert.Empty(await new SprintStore(issues).ListAsync(false));
        Assert.Empty(await new SprintProposalAuditStore(issues.Db).ListAsync());
    }
    [Fact]
    public async Task UngroomedRequiredMemberPreventsClaimingAcceptance()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var a = await ApprovedSprintFixture.Task(issues); var b = await ApprovedSprintFixture.Task(issues,false);
        await ApprovedSprintFixture.Bind(issues,scope,[a,b]);
        Assert.Null((await Planner(issues).BuildAsync()).Snapshot);
        Assert.Empty(await new SprintStore(issues).ListAsync(false));
    }

    [Fact]
    public async Task IncompletePrerequisiteUnitIsRejectedInsteadOfSilentlyDroppingPartner()
    {
        await using var issues = Store(); var scope = await ApprovedSprintFixture.Scope(issues);
        var goal = await ApprovedSprintFixture.Task(issues); var prerequisite = await ApprovedSprintFixture.Task(issues);
        var partner = await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues,scope,[goal]);
        await ApprovedSprintFixture.Bind(issues,scope,[prerequisite,partner]);
        await issues.AddDependencyAsync(prerequisite.Id,goal.Id,IssueDepKind.Blocks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SprintActivationValidator(issues,new SprintStore(issues),new SpecStore(issues),issues.ProjectId,null)
            .ValidateAsync([goal.Id],default));
    }
    [Fact]
    public async Task ExplicitRelatedApprovedSourcesCanShareOutcomeWithoutTitleMatching()
    {
        await using var issues = Store();
        var a=await ApprovedSprintFixture.Task(issues);var b=await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues,await ApprovedSprintFixture.Scope(issues),[a],criterion:"first-source");
        await ApprovedSprintFixture.Bind(issues,await ApprovedSprintFixture.Scope(issues),[b],criterion:"second-source");
        var result=await Planner(issues).BuildAsync();
        Assert.Equal(2,result.Snapshot!.TaskIds.Count);
        Assert.Equal(2,result.Snapshot.Plan!.Criteria.Select(c=>c.Scope.ProposalId).Distinct().Count());
    }
    [Fact]
    public async Task SequentialOptionalWorkDoesNotPretendToUseParallelSlots()
    {
        await using var issues=Store();var scope=await ApprovedSprintFixture.Scope(issues);
        var tasks=new List<IssueRecord>();
        for(var i=0;i<3;i++){var task=await ApprovedSprintFixture.Task(issues);tasks.Add(task);await ApprovedSprintFixture.Bind(issues,scope,[task],criterion:"c"+i);}
        await issues.AddDependencyAsync(tasks[0].Id,tasks[1].Id,IssueDepKind.Blocks);
        await issues.AddDependencyAsync(tasks[1].Id,tasks[2].Id,IssueDepKind.Blocks);
        var result=await Planner(issues,slots:3).BuildAsync();
        Assert.Equal(tasks[0].Id,Assert.Single(result.Snapshot!.TaskIds));
    }
    [Fact]
    public async Task StableInputsReusePlanAndZeroConfiguredRoleCapacityDefers()
    {
        await using var issues=Store();var scope=await ApprovedSprintFixture.Scope(issues);
        var task=await ApprovedSprintFixture.Task(issues);await ApprovedSprintFixture.Bind(issues,scope,[task]);
        var planner=Planner(issues);
        var first=await planner.BuildAsync();var second=await planner.BuildAsync();var third=await planner.BuildAsync();
        Assert.Same(first,second);Assert.Same(second,third);
        var unavailable=await Planner(issues,slots:0).BuildAsync();Assert.Null(unavailable.Snapshot);
        Assert.Contains(unavailable.Deferred,d=>d.Reason.Contains("capacity"));
    }
    [Fact]
    public async Task PendingMetadataUpdateCannotReviveFailedWork()
    {
        await using var issues=Store();var task=await ApprovedSprintFixture.Task(issues);
        await issues.TransitionAsync(task.Id,IssueStatus.Failed,"operator investigation");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>issues.MergePendingMetadataAsync(task.Id,new Dictionary<string,object>{["groomed"]=true}));
        Assert.Equal(IssueStatus.Failed,(await issues.GetAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task VisibilityOnlyBlockerNeedsGroomingAndAuditedAmendmentBeforeDispatch()
    {
        await using var issues=Store();var scope=await ApprovedSprintFixture.Scope(issues);
        var target=await ApprovedSprintFixture.Task(issues);await ApprovedSprintFixture.Bind(issues,scope,[target]);
        var sprints=new SprintStore(issues);
        var snapshot=(await Planner(issues).BuildAsync()).Snapshot!;
        var sprint=await sprints.CreateWithIssuesAsync(new("x","x",DateTime.UtcNow,DateTime.UtcNow.AddDays(1)),snapshot);
        var blocker=await ApprovedSprintFixture.Task(issues,false);
        await issues.AddDependencyAsync(blocker.Id,target.Id,IssueDepKind.Blocks);
        await sprints.AddIssueAsync(sprint.Id,blocker.Id); // sanctioned birth visibility, never execution authority
        Assert.NotNull(await SprintDispatchGuard.CheckAsync(issues,sprints,issues.ProjectId,blocker.Id,default));
        var validator=new SprintActivationValidator(issues,sprints,new SpecStore(issues),issues.ProjectId,null);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>validator.ValidateAsync([blocker.Id],default,activating:false));
        await ApprovedSprintFixture.Bind(issues,scope,[blocker]);
        await issues.MergePendingMetadataAsync(blocker.Id,new Dictionary<string,object>{["groomed"]=true});
        var amendment=await validator.ValidateAsync([blocker.Id],default,activating:false);
        await sprints.AddValidatedPrerequisiteAsync(sprint.Id,amendment);
        Assert.Null(await SprintDispatchGuard.CheckAsync(issues,sprints,issues.ProjectId,blocker.Id,default));
        Assert.Equal(2,(await new SprintProposalAuditStore(issues.Db).ListAsync()).Count);
    }
}
