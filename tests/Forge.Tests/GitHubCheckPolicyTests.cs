using Octokit;
using Xunit;

namespace Forge.Tests;

public class GitHubCheckPolicyTests
{
    [Theory]
    [InlineData(CheckConclusion.Cancelled)]
    [InlineData(CheckConclusion.Failure)]
    [InlineData(CheckConclusion.TimedOut)]
    [InlineData(CheckConclusion.ActionRequired)]
    public void CompletedUnsuccessfulCheck_CannotReleaseMerge(CheckConclusion conclusion)
    {
        Assert.Equal(CommitState.Failure, GitHubService.ClassifyCheckRuns(new[]
        {
            (CheckStatus.Completed, (CheckConclusion?)CheckConclusion.Success),
            (CheckStatus.Completed, (CheckConclusion?)conclusion),
        }));
    }

    [Fact]
    public void MissingConclusion_CannotReleaseMerge() => Assert.Equal(CommitState.Failure,
        GitHubService.ClassifyCheckRuns(new[] { (CheckStatus.Completed, (CheckConclusion?)null) }));

    [Fact]
    public void RunningCheck_RemainsPending() => Assert.Equal(CommitState.Pending,
        GitHubService.ClassifyCheckRuns(new[] { (CheckStatus.InProgress, (CheckConclusion?)null) }));

    [Fact]
    public void SuccessfulChecks_ReleaseMerge() => Assert.Equal(CommitState.Success,
        GitHubService.ClassifyCheckRuns(new[] { (CheckStatus.Completed, (CheckConclusion?)CheckConclusion.Success) }));
}
