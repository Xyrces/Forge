using Octokit;
using Forge.Configuration;

namespace Forge;

public class GitHubService
{
    private readonly GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubService(GitHubOptions options)
        : this(options.Owner, options.Repo, options.Token) { }

    public GitHubService(string owner, string repo, string? token = null)
    {
        _owner = owner;
        _repo = repo;
        _client = new GitHubClient(new ProductHeaderValue("Forge"));
        if (!string.IsNullOrEmpty(token))
            _client.Credentials = new Credentials(token);
    }

    /// <summary>Owner (org/user) for this GitHubService. Read-only
    /// in production; the e2e harness's subclass exposes it via
    /// <see cref="LocalGitHubService"/>.</summary>
    public string Owner => _owner;

    /// <summary>Repo name for this GitHubService.</summary>
    public string Repo => _repo;

    public virtual async Task<string> CreateBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        var mainRef = await _client.Git.Reference.Get(_owner, _repo, "heads/main");
        var newRef = new NewReference($"refs/heads/{branchName}", mainRef.Object.Sha);
        var result = await _client.Git.Reference.Create(_owner, _repo, newRef);
        return result.Ref;
    }

    public virtual async Task<PullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch = "main",
        CancellationToken cancellationToken = default)
    {
        var pr = new NewPullRequest(title, headBranch, baseBranch) { Body = body, MaintainerCanModify = true };
        return await _client.PullRequest.Create(_owner, _repo, pr);
    }

    /// <summary>
    /// Find the OPEN pull request whose head branch is
    /// <paramref name="headBranch"/>, or null. Used by the rework
    /// loop: a reworked task pushes to its existing branch, so the PR
    /// already exists and must be reused, not re-created (Octokit's
    /// Create throws ValidationException in that case).
    /// </summary>
    public virtual async Task<PullRequest?> GetOpenPullRequestForBranchAsync(
        string headBranch, CancellationToken cancellationToken = default)
    {
        var request = new PullRequestRequest { State = ItemStateFilter.Open };
        var open = await _client.PullRequest.GetAllForRepository(_owner, _repo, request);
        // Head.Ref is the branch name for same-repo PRs; match
        // case-insensitively (git refs are case-sensitive but the
        // orchestrator normalizes branch names at creation).
        return open.FirstOrDefault(p =>
            string.Equals(p.Head.Ref, headBranch, StringComparison.OrdinalIgnoreCase));
    }

    public virtual async Task<bool> MergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var merge = new MergePullRequest();
            var result = await _client.PullRequest.Merge(_owner, _repo, prNumber, merge);
            return result.Merged;
        }
        catch { return false; }
    }

    public virtual async Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
    {
        var response = await _client.Repository.Status.GetCombined(_owner, _repo, sha);
        return response.State.Value;
    }

    public virtual async Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int prNumber, CancellationToken cancellationToken = default)
        => await _client.PullRequest.Review.GetAll(_owner, _repo, prNumber);

    public virtual async Task<PullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        => await _client.PullRequest.Get(_owner, _repo, prNumber);

    public virtual async Task<bool> DeleteBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.Git.Reference.Delete(_owner, _repo, $"refs/heads/{branchName}");
            return true;
        }
        catch (NotFoundException) { return true; }
        catch { return false; }
    }

    /// <summary>
    /// 2026-07-18 (Phase 2.11.f + bug-1-review): post a non-blocking
    /// issue comment on the PR. Used by the Reviewer agent to leave an
    /// audit trail of what it reviewed, separate from the
    /// structured review event below.
    /// </summary>
    public virtual async Task<long> CreateIssueCommentAsync(
        long issueNumber, string body, CancellationToken cancellationToken = default)
    {
        var comment = await _client.Issue.Comment.Create(_owner, _repo, (int)issueNumber, body);
        return comment.Id;
    }

    /// <summary>
    /// 2026-07-18: submit a structured review event
    /// (Approved / ChangesRequested / Commented) on a PR. The
    /// Reviewer agent uses this to drive PRWatcher.ProcessWatchTaskAsync
    /// to GreenAndApproved without requiring a human reviewer.
    /// </summary>
    public virtual async Task<long> SubmitReviewAsync(
        int prNumber, string headSha, string body,
        PullRequestReviewState state, CancellationToken cancellationToken = default)
    {
        var review = new PullRequestReviewCreate
        {
            CommitId = headSha,
            Body = body,
            Event = state switch
            {
                PullRequestReviewState.Approved => PullRequestReviewEvent.Approve,
                PullRequestReviewState.ChangesRequested => PullRequestReviewEvent.RequestChanges,
                _ => PullRequestReviewEvent.Comment,
            },
        };
        var result = await _client.PullRequest.Review.Create(_owner, _repo, prNumber, review);
        return result.Id;
    }

    /// <summary>
    /// 2026-07-18: fetch the PR's unified diff text. Used by the
    /// Reviewer agent to inspect the engineer's changes before
    /// deciding on Approval / ChangesRequested.
    /// </summary>
    public virtual async Task<string> GetPullRequestDiffAsync(
        int prNumber, CancellationToken cancellationToken = default)
    {
        var pr = await _client.PullRequest.Get(_owner, _repo, prNumber);
        var headers = new System.Collections.Generic.Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github.v3.diff"
        };
        var diff = await _client.Connection.Get<string>(
            new Uri(pr.Url), headers, "application/vnd.github.v3.diff");
        return diff.Body ?? "";
    }
}
