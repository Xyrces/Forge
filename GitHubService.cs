using Octokit;
using PortHorizon.Agents.Configuration;

namespace PortHorizon.Agents;

public sealed class GitHubService
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
        _client = new GitHubClient(new ProductHeaderValue("PortHorizon-Agents"));
        if (!string.IsNullOrEmpty(token))
            _client.Credentials = new Credentials(token);
    }

    public async Task<string> CreateBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        var mainRef = await _client.Git.Reference.Get(_owner, _repo, "heads/main");
        var newRef = new NewReference($"refs/heads/{branchName}", mainRef.Object.Sha);
        var result = await _client.Git.Reference.Create(_owner, _repo, newRef);
        return result.Ref;
    }

    public async Task<PullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch = "main",
        CancellationToken cancellationToken = default)
    {
        var pr = new NewPullRequest(title, headBranch, baseBranch) { Body = body, MaintainerCanModify = true };
        return await _client.PullRequest.Create(_owner, _repo, pr);
    }

    public async Task<bool> MergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var merge = new MergePullRequest();
            var result = await _client.PullRequest.Merge(_owner, _repo, prNumber, merge);
            return result.Merged;
        }
        catch { return false; }
    }

    public async Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
    {
        var response = await _client.Repository.Status.GetCombined(_owner, _repo, sha);
        return response.State.Value;
    }

    public async Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int prNumber, CancellationToken cancellationToken = default)
        => await _client.PullRequest.Review.GetAll(_owner, _repo, prNumber);

    public async Task<PullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        => await _client.PullRequest.Get(_owner, _repo, prNumber);

    public async Task<bool> DeleteBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.Git.Reference.Delete(_owner, _repo, $"refs/heads/{branchName}");
            return true;
        }
        catch (NotFoundException) { return true; }
        catch { return false; }
    }
}
