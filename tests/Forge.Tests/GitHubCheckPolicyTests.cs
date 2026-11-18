using Octokit;
using Xunit;

namespace Forge.Tests;

public class GitHubCheckPolicyTests
{
    [Theory]
    [InlineData(1, 1, CommitState.Success, 1, CommitState.Failure, CommitState.Failure)]
    [InlineData(1, 1, CommitState.Success, 1, CommitState.Pending, CommitState.Pending)]
    [InlineData(1, 1, CommitState.Success, 0, CommitState.Pending, CommitState.Success)]
    [InlineData(0, 0, CommitState.Pending, 1, CommitState.Success, CommitState.Success)]
    [InlineData(0, 0, CommitState.Pending, 0, CommitState.Pending, CommitState.Pending)]
    [InlineData(2, 1, CommitState.Success, 1, CommitState.Success, CommitState.Pending)]
    [InlineData(1, 1, CommitState.Failure, 1, CommitState.Success, CommitState.Failure)]
    public void CurrentHeadMustSatisfyEveryCiSignal(int reported, int observed, CommitState checks,
        int contexts, CommitState statuses, CommitState expected)
        => Assert.Equal(expected, GitHubService.CombineCiSignals(reported, observed, checks, contexts, statuses));

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
