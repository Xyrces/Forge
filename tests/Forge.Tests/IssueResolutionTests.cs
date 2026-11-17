using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class IssueResolutionTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("resolution"));
    [Theory]
    [InlineData(CloseDispositionKind.Unknown, false)]
    [InlineData(CloseDispositionKind.Cancelled, false)]
    [InlineData(CloseDispositionKind.DeliveredElsewhere, true)]
    public async Task ClosedOnlySatisfiesDependencyWithExplicitDeliveryEvidence(CloseDispositionKind kind, bool satisfied)
    {
        var blocker = await issues.CreateAsync(new NewIssue("task", "Blocker"));
        var dependent = await issues.CreateAsync(new NewIssue("task", "Dependent"));
        await issues.AddDependencyAsync(blocker.Id, dependent.Id, IssueDepKind.Blocks);
        blocker = await issues.CloseWithDispositionAsync(blocker.Id, new(kind, "Operator disposition", "Evidence reference"));
        Assert.Equal(satisfied, IssueResolution.IsSatisfied(blocker));
        Assert.Equal(!satisfied, await issues.IsBlockedAsync(dependent.Id));
        Assert.Equal(satisfied, (await issues.ReadyAsync(10)).Any(t => t.Id == dependent.Id));
        await issues.TransitionAsync(blocker.Id, IssueStatus.Pending, null);
        await issues.TransitionAsync(blocker.Id, IssueStatus.Closed, "Later plain closure");
        Assert.True(await issues.IsBlockedAsync(dependent.Id));
    }
    [Fact]
    public async Task DuplicateRequiresCompletedReferenceAndReopenedReferenceBlocksAgain()
    {
        var delivered = await issues.CreateAsync(new NewIssue("task", "Delivered"));
        var duplicate = await issues.CreateAsync(new NewIssue("task", "Duplicate"));
        var dependent = await issues.CreateAsync(new NewIssue("task", "Dependent"));
        await issues.AddDependencyAsync(duplicate.Id, dependent.Id, IssueDepKind.Blocks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issues.CloseWithDispositionAsync(duplicate.Id,
            new(CloseDispositionKind.DuplicateDelivered, "Duplicate", "same acceptance", delivered.Id)));
        await issues.TransitionAsync(delivered.Id, IssueStatus.Completed, null);
        await issues.CloseWithDispositionAsync(duplicate.Id, new(CloseDispositionKind.DuplicateDelivered, "Duplicate", "same acceptance", delivered.Id));
        Assert.False(await issues.IsBlockedAsync(dependent.Id));
        await issues.TransitionAsync(delivered.Id, IssueStatus.Pending, null);
        Assert.True(await issues.IsBlockedAsync(dependent.Id));
    }
    public void Dispose() => issues.Dispose();
}
