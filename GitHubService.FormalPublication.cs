using Forge.Reviewer;
using Octokit;

namespace Forge;

public partial class GitHubService
{
    /// <summary>Only explicit test/local adapters may opt out of formal publication.</summary>
    public virtual bool RequireFormalReview => true;

    public virtual async Task EnsureReviewerIdentityAsync(int prNumber, CancellationToken ct)
    {
        if (!RequireFormalReview) return;
        if (_reviewClient is null || string.IsNullOrWhiteSpace(ReviewerLogin))
            throw new InvalidOperationException("Separate reviewer GitHub App is not configured. No paid review was started.");
        var pr = await _reviewClient.PullRequest.Get(_owner, _repo, prNumber).WaitAsync(ct);
        if (string.Equals(pr.User?.Login, ReviewerLogin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reviewer identity authored this PR; configure distinct publisher and reviewer Apps.");
    }

    public virtual async Task<PublishedReview?> FindPublishedReviewAsync(int prNumber, ReviewDraft draft, string marker, CancellationToken ct)
    {
        if (!RequireFormalReview) return null;
        if (_reviewClient is null || ReviewerLogin is null) throw new InvalidOperationException("Reviewer App is unavailable.");
        var all = await _reviewClient.PullRequest.Review.GetAll(_owner, _repo, prNumber).WaitAsync(ct);
        var state = draft.Verdict == nameof(ReviewerVerdict.Approve) ? PullRequestReviewState.Approved : PullRequestReviewState.ChangesRequested;
        var matching = all.Where(r => r.CommitId == draft.HeadSha && r.State == state
            && string.Equals(r.User?.Login, ReviewerLogin, StringComparison.OrdinalIgnoreCase)
            && r.Body?.Contains(marker, StringComparison.Ordinal) == true).OrderBy(r => r.Id).FirstOrDefault();
        return matching is null ? null : Published(draft, matching);
    }

    public virtual async Task<PublishedReview> PublishFormalReviewAsync(int prNumber, ReviewDraft draft, string marker, CancellationToken ct)
    {
        if (!RequireFormalReview)
        {
            await SubmitReviewAsync(prNumber, draft.HeadSha, draft.Summary + "\n\n" + marker,
                draft.Verdict == nameof(ReviewerVerdict.Approve) ? PullRequestReviewState.Approved : PullRequestReviewState.ChangesRequested, ct);
            return new(draft.Round, draft.HeadSha, draft.Verdict, draft.Summary, "Local test reviewer", "", DateTimeOffset.UtcNow, draft.Findings);
        }
        if (_reviewClient is null || ReviewerLogin is null) throw new InvalidOperationException("Reviewer App is unavailable.");
        var payload = new Dictionary<string, object>
        {
            ["commit_id"] = draft.HeadSha,
            ["event"] = draft.Verdict == nameof(ReviewerVerdict.Approve) ? "APPROVE" : "REQUEST_CHANGES",
            ["body"] = draft.Summary + "\n\n" + marker,
            ["comments"] = draft.Findings.Select(f => new { path = f.Path, line = f.Line, side = f.Side, body = f.Body }).ToArray()
        };
        var review = await new ApiConnection(_reviewClient.Connection).Post<PullRequestReview>(
            new Uri($"repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}/pulls/{prNumber}/reviews", UriKind.Relative), payload, ct);
        var expected = draft.Verdict == nameof(ReviewerVerdict.Approve) ? PullRequestReviewState.Approved : PullRequestReviewState.ChangesRequested;
        if (review.CommitId != draft.HeadSha || review.State != expected || !string.Equals(review.User?.Login, ReviewerLogin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub returned a review with unexpected identity, head or verdict; publication needs reconciliation.");
        return Published(draft, review);
    }

    private static PublishedReview Published(ReviewDraft draft, PullRequestReview review) => new(draft.Round,
        draft.HeadSha, draft.Verdict, draft.Summary, review.User.Login, review.HtmlUrl, review.SubmittedAt, draft.Findings);
}
