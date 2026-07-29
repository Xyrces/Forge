using Forge.Tools.BranchAudit;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Pure-function tests for the branch-audit tool's classifier and
/// protection predicate. The git/network side of the tool is covered
/// by a manual run captured in docs/BRANCH_AUDIT.md (regenerable via
/// `dotnet run --project tools/branch-audit`).
/// </summary>
public class BranchAuditTests
{
    [Theory]
    [InlineData("polecat/abc-1",          BranchClassifier.Polecat)]
    [InlineData("convoy/run-42",          BranchClassifier.Convoy)]
    [InlineData("gt1234",                 BranchClassifier.Gt)]
    [InlineData("gt1",                    BranchClassifier.Gt)]
    [InlineData("ph-abcd",                BranchClassifier.Ph)]
    [InlineData("ph/some/path",           BranchClassifier.Ph)]
    [InlineData("agent/task-1",           BranchClassifier.Agent)]
    [InlineData("agent/cutover-doc",      BranchClassifier.Agent)]
    [InlineData("POR-1115",               BranchClassifier.StalePor)]
    [InlineData("por-2222",               BranchClassifier.StalePor)]
    [InlineData("random/branch",          BranchClassifier.Other)]
    [InlineData("main",                   BranchClassifier.Other)]
    [InlineData("test/push-agent-12",     BranchClassifier.Other)]
    public void Classify_KnownPatterns(string branch, string expected)
    {
        Assert.Equal(expected, BranchClassifier.Classify(branch));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("develop")]
    [InlineData("HEAD")]
    public void IsProtected_AlwaysProtected(string branch)
    {
        Assert.True(BranchProtector.IsProtected(branch, new HashSet<string>()));
    }

    [Fact]
    public void IsProtected_ConfiguredTakesEffect()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "release/x", "hotfix/y" };
        Assert.True(BranchProtector.IsProtected("release/x", set));
        Assert.True(BranchProtector.IsProtected("hotfix/y", set));
        Assert.False(BranchProtector.IsProtected("feature/whatever", set));
    }

    [Fact]
    public void IsProtected_PolecatIsNotAlwaysProtected()
    {
        // polecat branches ARE deletable per the dead-fleet policy;
        // they only become protected if the operator explicitly adds them.
        Assert.False(BranchProtector.IsProtected("polecat/abc-1", new HashSet<string>()));
    }

    [Fact]
    public void Classify_AgentBeatsOther()
    {
        // defensive: agent/task-X is dead-fleet "agent", never "other".
        Assert.Equal(BranchClassifier.Agent, BranchClassifier.Classify("agent/task-9999"));
    }
}
