using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator.Sprint;
using Forge.Orchestrator;
using Forge.Projects;
using Forge.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace Forge.Tests;
/// <summary>Characterization of current planning gaps, not approval of their future policy.
/// These tests must change with an explicitly accepted planning contract.</summary>
public sealed class SprintPlanningAuditTests : IDisposable
{
    readonly string dir = TempRoot.Instance.NewDirectory("sprint-audit");
    readonly IssueStore issues;
    readonly SprintStore sprints;
    readonly SpecStore specs;
    readonly SprintAssembler assembler;
    public SprintPlanningAuditTests()
    {
        issues = new IssueStore(Path.Combine(dir,"issues.db"));
        sprints = new SprintStore(issues); specs = new SpecStore(issues);
        assembler = new SprintAssembler(new ProjectContextFactory(new List<ProjectOptions>()),
            new InMemoryDashboardEventBus(), NullLogger<SprintAssembler>.Instance);
    }
    public void Dispose() { issues.Dispose(); try { Directory.Delete(dir,true); } catch(IOException) { /* SQLite pooled handles are cleaned by test temp retention. */ } }
    Task Tick() => assembler.TickProjectAsync("audit", issues, sprints, specs, default);
    async Task<List<IssueRecord>> Spec(int count)
    {
        var spec = await specs.CreateAsync(new NewSpec("audit","One deliverable","body"));
        var story = await issues.CreateAsync(new NewIssue("story","story",ParentId:spec.Id));
        var tasks = new List<IssueRecord>();
        for(int i=0;i<count;i++) tasks.Add(await issues.CreateAsync(new NewIssue("task","task"+i,ParentId:story.Id)));
        await specs.SetStatusAsync(spec.Id,SpecStatus.Approved,default);
        await specs.SetStatusAsync(spec.Id,SpecStatus.Grooming,default);
        await specs.SetStatusAsync(spec.Id,SpecStatus.Groomed,default);
        return tasks;
    }
    async Task<IssueRecord> AdHoc(string title, string? followUp=null, bool groomed=true)
    {
        var metadata = new Dictionary<string,object>();
        if(groomed) metadata["groomed"]="true";
        if(followUp is not null) metadata["followUpOf"]=followUp;
        return await issues.CreateAsync(new NewIssue("task",title,Metadata:metadata));
    }
    [Fact] public async Task DependenciesRemainWithinOneSpecSprint()
    {
        var tasks=await Spec(2);
        await issues.AddDependencyAsync(tasks[0].Id,tasks[1].Id,IssueDepKind.Blocks,default);
        await Tick(); var first=(await sprints.GetActiveAsync())!;
        var members=await sprints.GetIssueIdsAsync(first.Id);
        Assert.Contains(tasks[0].Id,members); Assert.Contains(tasks[1].Id,members);
        await issues.TransitionAsync(tasks[0].Id,IssueStatus.Completed,null);
        await Tick(); var second=(await sprints.GetActiveAsync())!;
        Assert.Equal(first.Id,second.Id);
        Assert.Contains(tasks[1].Id,await sprints.GetIssueIdsAsync(second.Id));
    }
    [Fact] public async Task SpecTasksAreNotSubjectToFollowUpTenTaskCap()
    {
        var tasks=await Spec(12); await Tick();
        var ids=await sprints.GetIssueIdsAsync((await sprints.GetActiveAsync())!.Id);
        Assert.All(tasks,t=>Assert.Contains(t.Id,ids));
    }
    [Fact] public async Task SameSpecSiblingOriginsMakeSeparateFollowUpThemes()
    {
        var roots=await Spec(2);
        foreach(var root in roots) await issues.TransitionAsync(root.Id,IssueStatus.Completed,null);
        var a=await AdHoc("Authentication reliability",roots[0].Id);
        var b=await AdHoc("Authentication reliability",roots[1].Id);
        await Tick(); var ids=await sprints.GetIssueIdsAsync((await sprints.GetActiveAsync())!.Id);
        Assert.Single(new[]{a.Id,b.Id}.Where(ids.Contains));
    }
    [Fact] public async Task UnrelatedUngroomedFollowUpDoesNotStopReadySpec()
    {
        await Spec(2); await AdHoc("Unrelated docs follow-up","old-root",false);
        await Tick(); Assert.NotNull(await sprints.GetActiveAsync());
    }
    [Fact] public async Task LateParentedPrerequisiteJoinsActiveSprint()
    {
        var first=await Spec(1); await Tick(); var active=(await sprints.GetActiveAsync())!;
        var prerequisite=await Spec(1);
        await issues.AddDependencyAsync(prerequisite[0].Id,first[0].Id,IssueDepKind.Blocks,default);
        await Tick();
        Assert.Equal(active.Id,(await sprints.GetActiveAsync())!.Id);
        Assert.Contains(prerequisite[0].Id,await sprints.GetIssueIdsAsync(active.Id));
        Assert.Contains(await issues.ReadyAsync(100,active.Id), t=>t.Id == prerequisite[0].Id);
    }
    [Fact] public async Task CompletionInterruptionPersistsNeedsReviewWithoutAutomaticPaidRetry()
    {
        var root=await AdHoc("Deliver feature"); await Tick();
        var active=(await sprints.GetActiveAsync())!;
        var drafts=new FollowUpDraftStore(issues);
        for(int i=0;i<2;i++) await drafts.FileAsync(new FollowUpDraft(0,active.Id,root.Id,"Reviewer","finding"+i,"description",2,null,DateTime.UtcNow,null));
        await issues.TransitionAsync(root.Id,IssueStatus.Completed,null);
        var crashing=new SprintAssembler(new ProjectContextFactory(new List<ProjectOptions>()),
            new InMemoryDashboardEventBus(),NullLogger<SprintAssembler>.Instance,followUpTriage:new InterruptedTriage());
        await crashing.TickProjectAsync("audit",issues,sprints,specs,default);
        var completion = new Forge.Core.Completion.SprintCompletionStore(issues);
        Assert.Equal("NeedsReview", Assert.Single(await completion.ListBatchesAsync("completion-" + active.Id)).State);
        Assert.Null(await sprints.GetActiveAsync());
        await Tick();
        Assert.Equal(2,(await drafts.ListUnconsumedAsync()).Count);
        Assert.Single(await issues.ListAsync(new IssueFilter()));
    }
    [Fact] public async Task ProposalCommitCannotDisplaceActiveSprintWithUngroomedTask()
    {
        await AdHoc("Existing committed goal");
        await Tick();
        var active=(await sprints.GetActiveAsync())!;
        var ungroomed=await AdHoc("Ungroomed unrelated work",groomed:false);
        await using var audit=new SprintProposalAuditStore(issues.Db);
        var proposer=new SprintProposeService(issues,sprints,new Forge.Agents.DeterministicScorer(),audit);
        var proposal=await proposer.ProposeAsync(null,null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proposer.CommitAsync(proposal.AuditId,new[]{ungroomed.Id},null,null,"audit"));
        Assert.Equal(active.Id,(await sprints.GetActiveAsync())!.Id);
        Assert.Equal(SprintStatus.Active,(await sprints.GetAsync(active.Id))!.Status);
        Assert.DoesNotContain(ungroomed.Id,await sprints.GetIssueIdsAsync(active.Id));
    }
    sealed class InterruptedTriage : IFollowUpTriage
    {
        public Task<FollowUpTriageDecision?> TriageAsync(string projectId,IReadOnlyList<FollowUpDraft> drafts,CancellationToken ct=default)
            => throw new IOException("simulated interruption after sprint completion commit");
    }
}
