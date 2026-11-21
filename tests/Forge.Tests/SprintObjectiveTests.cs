using Forge.Core;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

public sealed class SprintObjectiveTests
{
    [Fact]
    public async Task NewPlanPersistsExplicitObjectiveWithoutReplacingItWithAcceptanceText()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("objective"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        const string objective = "Show the running Forge revision";
        var criteria = new[]
        {
            new SprintCriterion("build", "The exact revised tree passes the offline build and contract tests.", SprintEvidenceKind.MergedAndVerified, scope),
            new SprintCriterion("observe", "An operator confirms the deployed dashboard shows the exact running revision.", SprintEvidenceKind.OperatorObserved, scope)
        };
        await new SprintOutcomeAuthority(issues).BindAsync(task.Id,
            new(1, "running-revision", objective, criteria, [task.Id], ["build", "observe"], SprintMemberReason.Delivers, scope));
        var sprints = new SprintStore(issues);
        var validator = new SprintActivationValidator(issues, sprints, new SpecStore(issues), issues.ProjectId, null);

        var snapshot = await validator.ValidateAsync([task.Id], default);
        Assert.Equal(objective, snapshot.Plan!.Objective);
        Assert.Equal(criteria, snapshot.Plan.Criteria);
        Assert.True(snapshot.Plan.HasValidHash);
        var sprint = await sprints.CreateWithIssuesAsync(new("Revision", "ignored caller goal", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        var persisted = await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(sprint.Id);
        Assert.Equal(objective, persisted!.Objective);
        Assert.Equal(objective, (await sprints.GetAsync(sprint.Id))!.Goal);
        Assert.Equal(criteria, persisted.Criteria);
    }

    [Fact]
    public async Task NecessaryAmendmentRetainsCommittedObjectiveAndAllAcceptanceCriteria()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("objective-amendment"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var target = await ApprovedSprintFixture.Task(issues);
        const string objective = "Show the running Forge revision";
        await ApprovedSprintFixture.Bind(issues, scope, [target], objective: objective);
        var sprints = new SprintStore(issues);
        var validator = new SprintActivationValidator(issues, sprints, new SpecStore(issues), issues.ProjectId, null);
        var initial = await validator.ValidateAsync([target.Id], default);
        var sprint = await sprints.CreateWithIssuesAsync(new("Revision", "ignored", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), initial);
        var blocker = await ApprovedSprintFixture.Task(issues);
        await issues.AddDependencyAsync(blocker.Id, target.Id, IssueDepKind.Blocks);
        await ApprovedSprintFixture.Bind(issues, scope, [blocker], objective: "Prepare the necessary revision prerequisite");

        var amendment = await validator.ValidateAsync([blocker.Id], default, activating: false);
        Assert.Equal(objective, amendment.Plan!.Objective);
        Assert.Equal(initial.Plan!.Criteria, amendment.Plan.Criteria);
        Assert.True(amendment.Plan.HasValidHash);
        await sprints.AddValidatedPrerequisiteAsync(sprint.Id, amendment);
        var persisted = await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(sprint.Id);
        Assert.Equal(objective, persisted!.Objective);
        Assert.Equal(initial.Plan.Criteria, persisted.Criteria);
        Assert.Equal(2, persisted.Members.Count);
        Assert.Equal(objective, (await sprints.GetAsync(sprint.Id))!.Goal);
    }
}
