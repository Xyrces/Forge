using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class SprintDependencyStoreTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("closure"));
    private SprintStore Sprints => new(issues);
    private Task<IssueRecord> Row(string title) => issues.CreateAsync(new NewIssue("task", title,
        Metadata: new Dictionary<string, object> { ["groomed"] = true }));
    private static NewSprint Sprint() => new("Outcome", "Deliver outcome", DateTime.UtcNow, DateTime.UtcNow.AddDays(7));
    private async Task<SprintActivationSnapshot> Snapshot(params IssueRecord[] rows)
    {
        var edges = await issues.IncomingDependenciesAsync(rows.Select(r => r.Id).ToArray());
        var dependencies = new List<SprintDependencySnapshot>();
        foreach (var edge in edges)
            dependencies.Add(new(edge.BlockerId, edge.BlockedId, (await issues.GetAsync(edge.BlockerId))?.Status));
        return new(rows, [], rows.Select(r => r.Id).ToArray(), Dependencies: dependencies);
    }

    [Fact]
    public async Task ClosedMembershipIncludesChainButDispatchOnlyReturnsReadyRoot()
    {
        var a = await Row("A"); var b = await Row("B"); var c = await Row("C");
        await issues.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        await issues.AddDependencyAsync(b.Id, c.Id, IssueDepKind.Blocks);
        var sprint = await Sprints.CreateWithIssuesAsync(Sprint(), await Snapshot(a, b, c));
        Assert.Equal(3, (await Sprints.GetIssueIdsAsync(sprint.Id)).Count);
        Assert.Equal(a.Id, Assert.Single(await issues.ReadyAsync(10, sprint.Id)).Id);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("state")]
    public async Task ChangedDependencySnapshotRollsBackEntireActivation(string change)
    {
        var a = await Row("A"); var b = await Row("B");
        if (change != "add") await issues.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        var snapshot = await Snapshot(a, b);
        if (change == "add") await issues.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        if (change == "remove") await issues.RemoveDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        if (change == "state") await issues.TransitionAsync(a.Id, IssueStatus.Failed, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sprints.CreateWithIssuesAsync(Sprint(), snapshot));
        Assert.Empty(await Sprints.ListAsync(false));
    }

    [Fact]
    public async Task ExternalFailedPrerequisiteAndCycleAreRejected()
    {
        var a = await Row("A"); var b = await Row("B");
        await issues.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RejectExternal());
        async Task RejectExternal() => await Sprints.CreateWithIssuesAsync(Sprint(), await Snapshot(b));
        await issues.AddDependencyAsync(b.Id, a.Id, IssueDepKind.Blocks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RejectCycle());
        async Task RejectCycle() => await Sprints.CreateWithIssuesAsync(Sprint(), await Snapshot(a, b));
        await issues.RemoveDependencyAsync(b.Id, a.Id, IssueDepKind.Blocks);
        await issues.TransitionAsync(a.Id, IssueStatus.Failed, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RejectExternal());
    }

    [Fact]
    public async Task TransitivePrerequisiteAmendmentPreservesActiveObjective()
    {
        var target = await Row("Outcome");
        var active = await Sprints.CreateWithIssuesAsync(Sprint(), await Snapshot(target));
        var a = await Row("A"); var b = await Row("B");
        await issues.AddDependencyAsync(a.Id, b.Id, IssueDepKind.Blocks);
        await issues.AddDependencyAsync(b.Id, target.Id, IssueDepKind.Blocks);
        var combined = await Snapshot(a, b, target);
        var amendment = combined with { Issues = [a, b], TaskIds = [a.Id, b.Id], ExistingSprintMembers = [target] };
        await Sprints.AddValidatedPrerequisiteAsync(active.Id, amendment);
        Assert.Equal(3, (await Sprints.GetIssueIdsAsync(active.Id)).Count);
        Assert.Equal("Deliver outcome", (await Sprints.GetActiveAsync())!.Goal);
    }

    public void Dispose() => issues.Dispose();
}
