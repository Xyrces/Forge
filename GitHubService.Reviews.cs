using System.Text;
using Forge.Reviewer;
using Octokit;

namespace Forge;

public partial class GitHubService
{
    public const string HumanReviewLabel = "needs-human-review";

    internal static bool CanDecideReview(string? permission) => permission is "write" or "maintain" or "admin";
    internal static bool IsReviewIdentityEligible(string login, string? author, string? reviewer, bool bot)
        => !string.Equals(login, author, StringComparison.OrdinalIgnoreCase)
            && (!bot || string.Equals(login, reviewer, StringComparison.OrdinalIgnoreCase));

    public virtual async Task<IReadOnlyList<FormalDecision>> GetAuthorizedDecisionsAsync(
        int number, string? author, CancellationToken ct = default)
    {
        var reviews = await GetReviewsAsync(number, ct);
        var authorized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        List<FormalDecision> result = [];
        foreach (var review in reviews)
        {
            var login = review.User?.Login;
            if (string.IsNullOrEmpty(login) || !IsReviewIdentityEligible(login, author, ReviewerLogin,
                login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) || review.User?.Type == AccountType.Bot)) continue;
            var automated = string.Equals(login, ReviewerLogin, StringComparison.OrdinalIgnoreCase);
            if (!automated)
            {
                if (!authorized.TryGetValue(login, out var allowed))
                {
                    var permission = await _client.Repository.Collaborator.ReviewPermission(_owner, _repo, login).WaitAsync(ct);
                    authorized[login] = allowed = CanDecideReview(permission.Permission);
                }
                if (!allowed) continue;
            }
            result.Add(new(login, review.CommitId, review.State.Value, automated, review.Id, review.Body ?? "", review.HtmlUrl ?? ""));
        }
        return result;
    }

    public virtual async Task<bool> HasHumanReviewLabelAsync(int number, CancellationToken ct = default) =>
        (await _client.Issue.Labels.GetAllForIssue(_owner, _repo, number).WaitAsync(ct))
        .Any(label => string.Equals(label.Name, HumanReviewLabel, StringComparison.OrdinalIgnoreCase));

    public virtual async Task SetHumanReviewLabelAsync(int number, bool required, CancellationToken ct = default)
    {
        if (required)
        {
            await EnsureHumanReviewLabelAsync(ct);
            await _client.Issue.Labels.AddToIssue(_owner, _repo, number, [HumanReviewLabel]).WaitAsync(ct);
        }
        else if (await HasHumanReviewLabelAsync(number, ct))
            await _client.Issue.Labels.RemoveFromIssue(_owner, _repo, number, HumanReviewLabel).WaitAsync(ct);
    }

    private async Task EnsureHumanReviewLabelAsync(CancellationToken ct)
    {
        try
        {
            await _client.Issue.Labels.Get(_owner, _repo, HumanReviewLabel).WaitAsync(ct);
            return;
        }
        catch (NotFoundException) { /* First managed PR in this repository. */ }

        var label = new NewLabel(HumanReviewLabel, "0969DA") { Description = "Require human approval of the current revision before Forge merges." };
        try { await _client.Issue.Labels.Create(_owner, _repo, label).WaitAsync(ct); }
        catch (ApiValidationException)
        {
            // Concurrent PRs can both see a missing label. Proceed only if
            // the label is now available from the repository.
            await _client.Issue.Labels.Get(_owner, _repo, HumanReviewLabel).WaitAsync(ct);
        }
    }

    public virtual async Task<string> GetReviewReworkContextAsync(int number,
        IReadOnlyList<FormalDecision> requests, CancellationToken ct = default)
    {
        var comments = await _client.PullRequest.ReviewComment.GetAll(_owner, _repo, number).WaitAsync(ct);
        var text = new StringBuilder();
        foreach (var request in requests)
        {
            text.AppendLine($"Review by {request.Login} ({request.Url}) at {request.Head}:").AppendLine(request.Body);
            foreach (var comment in comments.Where(c => c.PullRequestReviewId == request.Id))
                text.AppendLine($"File: {comment.Path} ({comment.HtmlUrl})").AppendLine(comment.DiffHunk).AppendLine(comment.Body);
        }
        return text.ToString();
    }
}
