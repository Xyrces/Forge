using Forge.AgentTools;
using Xunit;

namespace Forge.Tests;

public sealed class WorkerPublicationPolicyTests
{
    [Theory]
    [InlineData("git push -u origin task")]
    [InlineData("git -C /repo push --force")]
    [InlineData("git send-pack origin HEAD")]
    [InlineData("git \\\n push origin HEAD")]
    [InlineData("git status && git push")]
    [InlineData("gh pr create --title test")]
    [InlineData("gh pr merge 1")]
    [InlineData("gh api repos/org/repo/pulls -X POST")]
    [InlineData("gh api repos/org/repo/pulls --method=PATCH")]
    [InlineData("gh api repos/org/repo/pulls -f title=test")]
    [InlineData("gh api graphql -f query=test")]
    public async Task Worker_RefusesPublicationRegardlessOfPlanApproval(string command)
    {
        foreach (var approved in new[] { false, true })
        {
            var tool = new BashTool("does-not-exist", mutationsAllowed: () => approved, publicationForbidden: true);
            var result = await tool.Bash(command);
            Assert.Contains("worker publication is disabled", result);
        }
    }

    [Theory]
    [InlineData("git commit -m test")]
    [InlineData("git add .")]
    [InlineData("git fetch origin")]
    [InlineData("dotnet test")]
    [InlineData("gh api repos/org/repo/pulls --method GET")]
    public void Worker_AllowsLocalWorkAndReads(string command)
        => Assert.False(WorkerPublicationPolicy.IsPublication(command));

    [Fact]
    public async Task LegacyBash_DoesNotApplyWorkerGuard()
    {
        var result = await new BashTool("does-not-exist").Bash("git push");
        Assert.Contains("workingDirectory", result);
        Assert.DoesNotContain("publication is disabled", result);
    }
}
