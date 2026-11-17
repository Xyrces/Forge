using Forge.Orchestrator.Workflow;
using Xunit;

namespace Forge.Tests;

public sealed class DispatchBranchTests
{
    [Fact]
    public void FreshDatasets_DoNotReuseLegacyOrEachOthersBranches()
    {
        var first = DispatchBranch.Resolve(null, "fresh-dataset-a", "forge", "task-2");
        var second = DispatchBranch.Resolve(null, "fresh-dataset-b", "forge", "task-2");
        Assert.NotEqual("agent/task-2", first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, DispatchBranch.Resolve(null, "fresh-dataset-a", "forge", "task-2"));
        Assert.NotEqual(first, DispatchBranch.Resolve(null, "fresh-dataset-a", "other-project", "task-2"));
        Assert.Matches("^agent/[a-f0-9]{16}/task-2$", first);
    }

    [Theory]
    [InlineData("agent/task-2")]
    [InlineData("operator/custom-branch")]
    public void Recovery_PreservesExplicitBranch(string branch)
        => Assert.Equal(branch, DispatchBranch.Resolve(branch, "fresh-dataset", "forge", "task-2"));

    [Fact]
    public void LegacyDispatch_KeepsExistingDefault()
        => Assert.Equal("agent/task-2", DispatchBranch.Resolve(null, null, "forge", "task-2"));
}
