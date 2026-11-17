using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Sprint;
using Forge.Tests.Integration.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class SprintVisibilityRetirementTests
{
    [Fact]
    public async Task GroomerRejectedVisibilityMemberIsRetiredBeforeExactCompletion()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("visibility"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues, scope, [task]);
        var sprints = new SprintStore(issues);
        var planner = new SprintPlanBuilder(issues, sprints, new SpecStore(issues), issues.ProjectId, null);
        var snapshot = (await planner.BuildAsync()).Snapshot!;
        var sprint = await sprints.CreateWithIssuesAsync(new("Delivery", "Delivery", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        var extra = await issues.CreateAsync(new("task", "Unneeded discovery", Metadata: new Dictionary<string, object> { ["followUpOf"] = task.Id }));
        await issues.AddDependencyAsync(extra.Id, task.Id, IssueDepKind.Blocks);
        await sprints.AddIssueAsync(sprint.Id, extra.Id);
        var client = new MultiToolCallingChatClient([new Microsoft.Extensions.AI.FunctionCallContent("close", "close_task", new Dictionary<string, object?> { ["reason"] = "Already covered by the implementation" })], "Closed");
        var groomer = new GroomerAgent(issues, new SpecStore(issues), new InMemoryDashboardEventBus(),
            new ScriptingChatClientFactory(client), new LlmConfig(new ProviderConfig("test", "", null, null, "test")),
            NullLogger<GroomerAgent>.Instance, projectId: "project");
        await groomer.GroomTaskAsync(extra.Id);
        var closed = (await issues.GetAsync(extra.Id))!;
        Assert.Equal(IssueStatus.Closed, closed.Status);
        await issues.TransitionAsync(task.Id, IssueStatus.Completed, null);
        var completion = new CompletionCoordinator(issues, null, null, NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.CompleteAsync(sprint, sprints, default));

        Assert.Equal(1, await sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        var receipt = await completion.CompleteAsync(sprint, sprints, default);
        Assert.Equal(Forge.Core.Completion.EngineeringOutcome.PendingEvidence, receipt.EngineeringOutcome);
        Assert.Equal(closed, await issues.GetAsync(extra.Id));
        Assert.Equal(task.Id, Assert.Single(await sprints.GetIssueIdsAsync(sprint.Id)));
        Assert.Equal(0, await sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        Assert.Contains(await issues.ListEventsAsync(extra.Id), e => e.Kind == "sprint_visibility_retired");
    }
    [Theory]
    [InlineData(IssueStatus.Pending)]
    [InlineData(IssueStatus.Failed)]
    [InlineData(IssueStatus.Blocked)]
    [InlineData(IssueStatus.Completed)]
    public async Task NonRejectedStatesNeverRetire(IssueStatus status)
    {
        var (issues, sprints, sprint, task, extra, scope) = await Seed();
        await using var owned = issues;
        await issues.TransitionAsync(extra.Id, status, null);
        Assert.Equal(0, await sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        Assert.Contains(extra.Id, await sprints.GetIssueIdsAsync(sprint.Id));
    }

    [Theory]
    [InlineData("prNumber", "123")]
    [InlineData("dispatchId", "attempt")]
    [InlineData("followUpOf", "foreign")]
    [InlineData("groomCloseReason", "")]
    public async Task MissingProvenanceOrEngineeringHistoryPreventsRetirement(string key, string value)
    {
        var (issues, sprints, sprint, task, extra, scope) = await Seed();
        await using var owned = issues;
        await issues.MergePendingMetadataAsync(extra.Id, new Dictionary<string, object> { [key] = value });
        await issues.TransitionAsync(extra.Id, IssueStatus.Closed, null);
        Assert.Equal(0, await sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        Assert.Contains(extra.Id, await sprints.GetIssueIdsAsync(sprint.Id));
    }

    [Fact]
    public async Task CommittedAmendmentMemberCannotBeRetiredWhenLaterClosed()
    {
        var (issues, sprints, sprint, task, extra, scope) = await Seed();
        await using var owned = issues;
        await ApprovedSprintFixture.Bind(issues, scope, [extra]);
        await issues.MergePendingMetadataAsync(extra.Id, new Dictionary<string, object> { ["groomed"] = true });
        var validator = new SprintActivationValidator(issues, sprints, new SpecStore(issues), issues.ProjectId, null);
        var snapshot = await validator.ValidateAsync([extra.Id], default, activating: false);
        await sprints.AddValidatedPrerequisiteAsync(sprint.Id, snapshot);
        await issues.TransitionAsync(extra.Id, IssueStatus.Closed, null);
        Assert.Equal(0, await sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        Assert.Contains(extra.Id, await sprints.GetIssueIdsAsync(sprint.Id));
    }

    [Fact]
    public async Task RetirementRejectsStaleAmendmentAndAuditFailureRollsBackDeletion()
    {
        var (issues, sprints, sprint, task, extra, scope) = await Seed();
        await using var owned = issues;
        await ApprovedSprintFixture.Bind(issues, scope, [extra]);
        await issues.MergePendingMetadataAsync(extra.Id, new Dictionary<string, object> { ["groomed"] = true });
        var validator = new SprintActivationValidator(issues, sprints, new SpecStore(issues), issues.ProjectId, null);
        var stale = await validator.ValidateAsync([extra.Id], default, activating: false);
        await issues.TransitionAsync(extra.Id, IssueStatus.Closed, null);
        await using var connection = await issues.Db.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_retirement BEFORE INSERT ON issue_event WHEN NEW.kind='sprint_visibility_retired' BEGIN SELECT RAISE(ABORT,'test audit failure'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        Assert.Contains(extra.Id, await sprints.GetIssueIdsAsync(sprint.Id));
        Assert.DoesNotContain(await issues.ListEventsAsync(extra.Id), e => e.Kind == "sprint_visibility_retired");
        command.CommandText = "DROP TRIGGER fail_retirement";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(1, await sprints.RetireClosedVisibilityMembersAsync(sprint.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sprints.AddValidatedPrerequisiteAsync(sprint.Id, stale));
        Assert.DoesNotContain(extra.Id, await sprints.GetIssueIdsAsync(sprint.Id));
        Assert.Single(await new SprintProposalAuditStore(issues.Db).ListAsync());
    }

    private static async Task<(IssueStore Issues, SprintStore Sprints, SprintRecord Sprint, IssueRecord Task, IssueRecord Extra, SprintScopeReference Scope)> Seed()
    {
        var issues = new IssueStore(TempRoot.Instance.NewDbPath("visibility"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        await ApprovedSprintFixture.Bind(issues, scope, [task]);
        var sprints = new SprintStore(issues);
        var planner = new SprintPlanBuilder(issues, sprints, new SpecStore(issues), issues.ProjectId, null);
        var snapshot = (await planner.BuildAsync()).Snapshot!;
        var sprint = await sprints.CreateWithIssuesAsync(new("Delivery", "Delivery", DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), snapshot);
        var extra = await issues.CreateAsync(new("task", "Rejected discovery", Metadata: new Dictionary<string, object>
        {
            ["followUpOf"] = task.Id, ["groomCloseReason"] = "Not necessary", ["groomRunId"] = "groom-test"
        }));
        await issues.AddDependencyAsync(extra.Id, task.Id, IssueDepKind.Blocks);
        await sprints.AddIssueAsync(sprint.Id, extra.Id);
        return (issues, sprints, sprint, task, extra, scope);
    }

}
