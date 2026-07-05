using Octokit;
using Forge.Reviewer;
using Xunit;

namespace Forge.Tests;

public class PRWatcherTests
{
    [Fact]
    public void EvaluateVerdict_CiFailure_CiFailed()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(CommitState.Failure, Array.Empty<PullRequestReviewState>());
        Assert.Equal(ReviewVerdict.CiFailed, verdict);
    }

    [Fact]
    public void EvaluateVerdict_CiError_CiFailed()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(CommitState.Error, Array.Empty<PullRequestReviewState>());
        Assert.Equal(ReviewVerdict.CiFailed, verdict);
    }

    [Fact]
    public void EvaluateVerdict_GreenAndApproved_GreenAndApproved()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(
            CommitState.Success,
            new[] { PullRequestReviewState.Approved });
        Assert.Equal(ReviewVerdict.GreenAndApproved, verdict);
    }

    [Fact]
    public void EvaluateVerdict_GreenChangesRequested_GreenChangesRequested()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(
            CommitState.Success,
            new[] { PullRequestReviewState.ChangesRequested });
        Assert.Equal(ReviewVerdict.GreenChangesRequested, verdict);
    }

    [Fact]
    public void EvaluateVerdict_GreenNoReview_Pending()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(CommitState.Success, Array.Empty<PullRequestReviewState>());
        Assert.Equal(ReviewVerdict.Pending, verdict);
    }

    [Fact]
    public void EvaluateVerdict_PendingCi_Pending()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(
            CommitState.Pending,
            new[] { PullRequestReviewState.Approved });
        Assert.Equal(ReviewVerdict.Pending, verdict);
    }

    [Fact]
    public void EvaluateVerdict_GreenApprovedAndChangesRequested_GreenAndApproved()
    {
        var verdict = PRWatcher.EvaluateVerdictFromStates(
            CommitState.Success,
            new[] { PullRequestReviewState.ChangesRequested, PullRequestReviewState.Approved });
        Assert.Equal(ReviewVerdict.GreenAndApproved, verdict);
    }

    [Fact]
    public void EvaluateVerdict_FromRealReviews_WrapsStatesApi()
    {
        var reviews = new List<PullRequestReview>
        {
            NewReviewWithState(PullRequestReviewState.Approved)
        };
        var verdict = PRWatcher.EvaluateVerdict(CommitState.Success, reviews);
        Assert.Equal(ReviewVerdict.GreenAndApproved, verdict);
    }

    private static PullRequestReview NewReviewWithState(PullRequestReviewState state)
    {
        var review = new PullRequestReview();
        var prop = typeof(PullRequestReview).GetProperty(nameof(PullRequestReview.State));
        var stringEnum = new Octokit.StringEnum<PullRequestReviewState>(state);
        prop!.SetValue(review, stringEnum);
        return review;
    }
}