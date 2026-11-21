using System.Text.Json;
using Forge.Core;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

public sealed class SprintOutcomePhaseTests
{
    [Theory]
    [InlineData(SprintEvidenceKind.OperatorObserved)]
    [InlineData(SprintEvidenceKind.DeploymentHealthy)]
    public async Task ObservationOnlyTaskCannotBindEvenWhenAnotherMemberDeliversCode(SprintEvidenceKind evidenceKind)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("phase"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var implementation = await ApprovedSprintFixture.Task(issues);
        var observation = await ApprovedSprintFixture.Task(issues);
        var binding = Binding(scope, [implementation.Id, observation.Id], evidenceKind);
        await new SprintOutcomeAuthority(issues).BindAsync(implementation.Id, binding);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SprintOutcomeAuthority(issues).BindAsync(observation.Id, binding with { CriterionIds = ["observe"] }));

        Assert.Contains("same outcome", error.Message);
        Assert.Null(SprintPlanJson.ReadBinding((await issues.GetAsync(observation.Id))!));
    }

    [Theory]
    [InlineData(SprintEvidenceKind.OperatorObserved)]
    [InlineData(SprintEvidenceKind.DeploymentHealthy)]
    public void EntireObservationOnlyUnitIsRejected(SprintEvidenceKind evidenceKind)
    {
        var scope = new SprintScopeReference("spec", "proposal", 1, "hash", "approved");
        var binding = Binding(scope, ["qa-1"], evidenceKind);
        Assert.Throws<InvalidOperationException>(() => SprintOutcomeAuthority.ValidateShape("qa-1",
            binding with { Criteria = [binding.Criteria[1]], CriterionIds = ["observe"] }));
    }

    [Fact]
    public async Task MixedEvidenceStaysInOneDeployableImplementationUnit()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("phase"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        await new SprintOutcomeAuthority(issues).BindAsync(task.Id, Binding(scope, [task.Id], SprintEvidenceKind.OperatorObserved));
        var planner = new SprintPlanBuilder(issues, new SprintStore(issues), new SpecStore(issues), issues.ProjectId,
            null, new(), new Dictionary<string, int> { ["coredev"] = 2 });

        var result = await planner.BuildAsync();

        Assert.Equal(task.Id, Assert.Single(result.Snapshot!.TaskIds));
        Assert.Contains(result.Snapshot.Plan!.Criteria, c => c.EvidenceKind == SprintEvidenceKind.OperatorObserved);
        Assert.Contains(result.Snapshot.Plan.Criteria, c => c.EvidenceKind == SprintEvidenceKind.MergedAndVerified);
    }

    [Fact]
    public async Task PersistedLegacyObservationOnlyBindingCannotEnterNewSprint()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("phase"), "project");
        var scope = await ApprovedSprintFixture.Scope(issues);
        var task = await ApprovedSprintFixture.Task(issues);
        var invalid = Binding(scope, [task.Id], SprintEvidenceKind.OperatorObserved) with { CriterionIds = ["observe"] };
        await issues.MergePendingMetadataAsync(task.Id, new Dictionary<string, object>
        {
            [SprintPlanJson.MetadataKey] = JsonSerializer.SerializeToElement(invalid, SprintPlanJson.Options)
        });
        var planner = new SprintPlanBuilder(issues, new SprintStore(issues), new SpecStore(issues), issues.ProjectId,
            null, new(), new Dictionary<string, int> { ["coredev"] = 2 });

        var result = await planner.BuildAsync();

        Assert.Null(result.Snapshot);
        Assert.Contains(result.Deferred, d => d.TaskId == task.Id && d.Reason.Contains("MergedAndVerified"));
    }

    private static SprintOutcomeBinding Binding(SprintScopeReference scope, string[] members, SprintEvidenceKind later) =>
        new(1, "delivery", "Implement and qualify the approved behavior",
            [new("code", "Implement and verify behavior", SprintEvidenceKind.MergedAndVerified, scope),
             new("observe", "Qualify deployed behavior", later, scope)],
            members, ["code", "observe"], SprintMemberReason.Delivers, scope);
}
