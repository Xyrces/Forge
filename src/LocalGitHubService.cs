using System.Collections.Concurrent;
using System.Diagnostics;
using Octokit;

namespace Forge;

/// <summary>
/// E2E harness adapter — implements the same surface as
/// <see cref="GitHubService"/> against a local bare git
/// repository. Records PRs in-process; "merge" / "review" /
/// "commit status" state is controlled by the harness via
/// the <see cref="LocalPrStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not used in production. The harness binary (tools/e2e-harness)
/// is the only caller. Production wires the real
/// <see cref="GitHubService"/> via the appsettings.json
/// <c>github</c> block.
/// </para>
/// <para>
/// Implemented as a <see cref="GitHubService"/> subclass so
/// the orchestrator's call sites don't change; the harness
/// swaps the constructor argument for an instance of this
/// class. All 7 public methods on the base class are
/// <c>virtual</c> to enable the override.
/// </para>
///
/// <para>
/// <b>Scope:</b> the harness verifies "the orchestrator opened
/// the right PR with the right diff". It does NOT verify the
/// PRWatcher verdict loop (PR merge + branch delete + final
/// issue Completed transition). That flow still uses the real
/// GitHubService call sites; the harness short-circuits the
/// merge step to keep the test bounded. Stage B's webhook-driven
/// PR merge signal will close that gap; until then the
/// integration test surface is "PR was opened correctly",
/// which is the operator's primary acceptance criterion.
/// </para>
/// </remarks>
public sealed class LocalGitHubService : GitHubService
{
    private readonly LocalPrStore _prStore;
    private readonly string _localRemotePath;

    /// <param name="localRemotePath">Path to the bare git
    /// repository that acts as the "remote". Set up by the
    /// harness via <c>git init --bare</c>.</param>
    /// <param name="owner">Owner string the orchestrator reads
    /// off <see cref="GitHubService.Owner"/>. Used only for
    /// assertion-side identity; the bare git doesn't care.</param>
    /// <param name="repo">Repo name. Same comment as
    /// <paramref name="owner"/>.</param>
    public LocalGitHubService(string localRemotePath, string owner, string repo)
        : base(owner, repo, token: null)
    {
        _localRemotePath = localRemotePath;
        _prStore = new LocalPrStore();
    }

    public LocalPrStore PrStore => _prStore;
    public string LocalRemotePath => _localRemotePath;

    /// <summary>
    /// The harness invokes this when the agent pushes the
    /// branch to the local remote (via
    /// <c>git push</c>). Records the head SHA so
    /// <see cref="CreatePullRequestAsync"/> returns a real one.
    /// </summary>
    public void RegisterPushedBranch(string branch, string sha)
    {
        _prStore.RegisterPushedBranch(branch, sha);
    }

    public override async Task<string> CreateBranchAsync(string branchName, CancellationToken cancellationToken = default)
    {
        // The harness's GitWorktreeService is configured with
        // the local remote + a real local clone. By the time
        // CreateBranchAsync is called (via CommitPushPrExecutor
        // for the worktree executor's flow), the branch has
        // usually already been pushed. Wait for it.
        var sha = await _prStore.WaitForBranchShaAsync(branchName, TimeSpan.FromSeconds(60), cancellationToken);
        return $"refs/heads/{branchName}";
    }

    public override async Task<PullRequest> CreatePullRequestAsync(
        string title, string body, string headBranch, string baseBranch = "main",
        CancellationToken cancellationToken = default)
    {
        var sha = await _prStore.WaitForBranchShaAsync(headBranch, TimeSpan.FromSeconds(60), cancellationToken);
        return _prStore.CreatePr(title, body, headBranch, baseBranch, sha);
    }

    public override Task<bool> MergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        // P4 e2e-harness C2: when the PRWatcher reaches the
        // GreenAndApproved verdict, the orchestrator's
        // CommitPushPrExecutor merges the PR. We record the
        // merge in the PR's state so the harness can assert.
        _prStore.MarkMerged(prNumber);
        return Task.FromResult(true);
    }

    public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(_prStore.GetCommitStatus(sha));

    public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int prNumber, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PullRequestReview>>(Array.Empty<PullRequestReview>());

    public override Task<PullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        => Task.FromResult(_prStore.GetPr(prNumber));

    public override Task<bool> DeleteBranchAsync(string branchName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>
/// In-process store of fake PRs + branches + (mocked) commit
/// statuses. PRs are recorded in
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
/// so the harness can manipulate state from a different thread
/// (a background approval loop) while the orchestrator's
/// polling loop reads.
/// </summary>
public sealed class LocalPrStore
{
    private readonly ConcurrentDictionary<int, PullRequest> _prs = new();
    private readonly ConcurrentDictionary<string, string> _branchShas = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _branchShaWaiters = new();
    private readonly ConcurrentDictionary<string, CommitState> _commitStates = new();
    private readonly ConcurrentDictionary<int, bool> _mergedFlags = new();
    private int _nextPrNumber = 1;

    public ICollection<PullRequest> AllPrs => _prs.Values;

    /// <summary>
    /// Mark a SHA as green (CI passed). Called by the e2e
    /// harness before driving the PRWatcher to simulate a
    /// green CI run. <see cref="LocalGitHubService.GetCommitStatusAsync"/>
    /// reads from this map.
    /// </summary>
    public void MarkCiGreen(string sha)
    {
        _commitStates[sha] = CommitState.Success;
    }

    /// <summary>
    /// Mark a SHA as failed (CI red).
    /// </summary>
    public void MarkCiRed(string sha)
    {
        _commitStates[sha] = CommitState.Failure;
    }

    public CommitState GetCommitStatus(string sha)
        => _commitStates.TryGetValue(sha, out var s) ? s : CommitState.Pending;

    /// <summary>
    /// Mark a PR as merged. Called by
    /// <see cref="LocalGitHubService.MergePullRequestAsync"/>
    /// when the orchestrator's PRWatcher reaches the
    /// GreenAndApproved verdict.
    /// </summary>
    public void MarkMerged(int prNumber)
    {
        _mergedFlags[prNumber] = true;
    }

    /// <summary>
    /// Test query — returns true if the PR was merged via
    /// <see cref="LocalGitHubService.MergePullRequestAsync"/>.
    /// </summary>
    public bool WasMerged(int prNumber)
        => _mergedFlags.TryGetValue(prNumber, out var v) && v;

    public void RegisterPushedBranch(string branch, string sha)
    {
        _branchShas[branch] = sha;
        if (_branchShaWaiters.TryRemove(branch, out var tcs))
        {
            tcs.TrySetResult(sha);
        }
    }

    public Task<string> WaitForBranchShaAsync(string branch, TimeSpan timeout, CancellationToken ct)
    {
        if (_branchShas.TryGetValue(branch, out var existing)) return Task.FromResult(existing);
        var tcs = _branchShaWaiters.GetOrAdd(branch, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_branchShas.TryGetValue(branch, out var just))
        {
            tcs.TrySetResult(just);
            _branchShaWaiters.TryRemove(branch, out _);
        }
        if (ct.CanBeCanceled)
        {
            ct.Register(() => tcs.TrySetCanceled(ct));
        }
        return tcs.Task.WaitAsync(timeout, ct);
    }

    public PullRequest CreatePr(string title, string body, string headBranch, string baseBranch, string headSha)
    {
        var number = Interlocked.Increment(ref _nextPrNumber);
        // Octokit.PullRequest is sealed + init-only. The only
        // public ctor that doesn't require all 50 fields is
        // (int number). Title/Body/Head/etc. are read-only and
        // end up empty. The harness keeps the real values in
        // a side dict (PrMeta below) so the assertions can read
        // them.
        var pr = new PullRequest(number);
        _prs[number] = pr;
        PrInfo[number] = new PrInfo(title, body, headBranch, baseBranch, headSha);
        return pr;
    }

    public ConcurrentDictionary<int, PrInfo> PrInfo { get; } = new();

    public PullRequest GetPr(int number)
    {
        if (!_prs.TryGetValue(number, out var pr)) throw new InvalidOperationException($"PR #{number} not found");
        return pr;
    }
}

/// <summary>
/// Side-channel record of a PR's title / body / branches /
/// head SHA. Octokit's <see cref="Octokit.PullRequest"/> is
/// sealed + init-only + only constructible via the (int number)
/// ctor; the harness keeps the meaningful fields here so the
/// assertions can read them.
/// </summary>
public sealed record PrInfo(string Title, string Body, string HeadBranch, string BaseBranch, string HeadSha);