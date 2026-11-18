using System.Collections.Concurrent;
using System.Linq;
using System.Diagnostics;
using Octokit;
using Forge.Reviewer;

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
/// class. All 13 public methods on the base class are
/// <c>virtual</c> to enable the override.
/// </para>
///
/// <para>
/// <b>Scope:</b> engineering dispatch and PRWatcher lifecycle against a
/// simulated local GitHub service. Approval and CI are supplied by the
/// harness. Merge observation models a fast-forward at the accepted head;
/// it does not assert that a real GitHub merge or CI service ran.
/// </para>
/// </remarks>
public sealed class LocalGitHubService : GitHubService
{
    public override bool RequireFormalReview => false;
    public override string? ReviewerLogin => "local-reviewer[bot]";

    public override Task EnsureReviewerIdentityAsync(int prNumber, CancellationToken ct) => Task.CompletedTask;

    public override Task<PublishedReview?> FindPublishedReviewAsync(int prNumber, ReviewDraft draft, string marker, CancellationToken ct)
        => Task.FromResult(_prStore.ReviewReceipts.GetValueOrDefault((prNumber, marker)));

    public override Task<PublishedReview> PublishFormalReviewAsync(int prNumber, ReviewDraft draft, string marker, CancellationToken ct)
    {
        var receipt = _prStore.ReviewReceipts.GetOrAdd((prNumber, marker), _ => new(draft.Round,
            draft.HeadSha, draft.Verdict, draft.Summary, ReviewerLogin!, "", DateTimeOffset.UtcNow, draft.Findings));
        return Task.FromResult(receipt);
    }

    public override Task<IReadOnlyList<FormalDecision>> GetAuthorizedDecisionsAsync(int number, string? author, CancellationToken ct = default)
    {
        var decisions = _prStore.ReviewReceipts.Where(pair => pair.Key.Number == number).Select(pair => pair.Value)
            .Select(review => new FormalDecision(review.Reviewer, review.HeadSha,
                review.Verdict == nameof(ReviewerVerdict.Approve) ? PullRequestReviewState.Approved : PullRequestReviewState.ChangesRequested,
                true, review.Round, review.Summary, review.Url)).ToList();
        return Task.FromResult<IReadOnlyList<FormalDecision>>(decisions);
    }

    public override Task<ProtectionReviewDecision> GetProtectionReviewDecisionAsync(int number, string head, CancellationToken ct = default)
        => Task.FromResult(ProtectionReviewDecision.NotRequired);

    public override Task<MergeRequirements> GetMergeRequirementsAsync(int number, string head, CancellationToken ct = default)
        => Task.FromResult(new MergeRequirements("CLEAN", []));

    public override Task ResolveReviewedConversationsAsync(int number, ReviewDraft draft, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task<bool> HasHumanReviewLabelAsync(int number, CancellationToken ct = default)
        => Task.FromResult(_prStore.HumanReviewLabels.ContainsKey(number));

    public override Task SetHumanReviewLabelAsync(int number, bool required, CancellationToken ct = default)
    {
        if (required) _prStore.HumanReviewLabels.TryAdd(number, true);
        else _prStore.HumanReviewLabels.TryRemove(number, out _);
        return Task.CompletedTask;
    }

    public override Task<string> GetReviewReworkContextAsync(int number, IReadOnlyList<FormalDecision> requests, CancellationToken ct = default)
        => Task.FromResult(string.Join("\n", requests.Select(request => request.Body)));

    public override Task<LegacyReviewHistoryPage> ReadReviewHistoryAsync(int prNumber, CancellationToken ct) => ReadLegacyReviewHistoryAsync(prNumber, ct);

    public override Task<LegacyReviewHistoryPage> ReadLegacyReviewHistoryAsync(int prNumber, CancellationToken ct)
        => Task.FromResult(new LegacyReviewHistoryPage([], false, []));

    public override Task<ReviewCodeExcerpt?> ReadReviewExcerptAsync(string sha, string path, int first, int last, CancellationToken ct)
        => Task.FromResult<ReviewCodeExcerpt?>(null);
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

    public override async Task<bool> MergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default, string? expectedHeadSha = null)
        => (await TryMergePullRequestAsync(prNumber, cancellationToken, expectedHeadSha)).Merged;

    public override Task<PullRequestMergeAttempt> TryMergePullRequestAsync(int prNumber, CancellationToken cancellationToken = default, string? expectedHeadSha = null)
    {
        // P4 e2e-harness C2: when the PRWatcher reaches the
        // GreenAndApproved verdict, the orchestrator's
        // CommitPushPrExecutor merges the PR. We record the
        // merge in the PR's state so the harness can assert.
        if (expectedHeadSha is not null
            && (!_prStore.PrInfo.TryGetValue(prNumber, out var info)
                || _prStore.GetBranchSha(info.HeadBranch) != expectedHeadSha))
            return Task.FromResult(new PullRequestMergeAttempt(false, "The PR head changed."));
        _prStore.MarkMerged(prNumber);
        return Task.FromResult(new PullRequestMergeAttempt(true));
    }

    public override Task<PullRequest> ClosePullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        // The local harness has no close-unmerged concept; closing is
        // a no-op state-wise (PR stays open in PrInfo) — the operator
        // close-obsolete path only needs the call to succeed.
        return Task.FromResult(new PullRequest());
    }

    public override Task<CommitState> GetCommitStatusAsync(string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(_prStore.GetCommitStatus(sha));

    /// <summary>The local harness always has CI signal (its status
    /// store is the signal) — never report missing so the watch's
    /// CI-never-fired retrigger stays out of the closed loop.</summary>
    public override Task<int> GetCiSignalCountAsync(string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(1);

    public override Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int prNumber, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PullRequestReview>>(Array.Empty<PullRequestReview>());

    /// <summary>
    /// Queries the local <see cref="LocalPrStore.PrInfo"/> for an open
    /// pull request whose head branch matches <paramref name="headBranch"/>.
    /// The match is case-insensitive (<see cref="StringComparison.OrdinalIgnoreCase"/>)
    /// and excludes already-merged PRs. Returns <c>null</c> when no
    /// open PR matches.
    /// </summary>
    public override Task<PullRequest?> GetOpenPullRequestForBranchAsync(
        string headBranch, CancellationToken cancellationToken = default)
    {
        var match = _prStore.PrInfo.FirstOrDefault(kvp =>
            string.Equals(kvp.Value.HeadBranch, headBranch, StringComparison.OrdinalIgnoreCase) &&
            !_prStore.WasMerged(kvp.Key));
        if (match.Value is null)
            return Task.FromResult<PullRequest?>(null);
        return Task.FromResult<PullRequest?>(_prStore.GetPr(match.Key));
    }

    public override Task<PullRequest> GetPullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
        => Task.FromResult(_prStore.GetPr(prNumber));

    public override Task<bool> DeleteBranchAsync(string branchName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
    public override Task<string> GetBranchHeadShaAsync(string branch, CancellationToken cancellationToken = default)
        => Task.FromResult(_prStore.GetBranchSha(branch) ?? $"local-{branch}-head");

    public override Task<IReadOnlyList<string>> GetFailedCheckRunSummariesAsync(string sha, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// Post a comment on a PR issue. Stored in-memory; no-op
    /// for the e2e harness (the harness asserts on PR creation
    /// and merge, not on comments).
    /// </summary>
    public override Task<long> CreateIssueCommentAsync(
        long issueNumber, string body, CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <summary>
    /// Submit a structured review event. Stored in-memory; no-op
    /// for the e2e harness.
    /// </summary>
    public override Task<long> SubmitReviewAsync(
        int prNumber, string headSha, string body,
        PullRequestReviewState state, CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <summary>
    /// Fetch the unified diff text for a PR. The harness stores
    /// the PR's metadata; for a real diff, the orchestrator would
    /// need to git-diff the head vs base. For now, return empty
    /// (the e2e harness doesn't drive the reviewer loop).
    /// </summary>
    public override Task<string> GetPullRequestDiffAsync(
        int prNumber, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    /// <summary>Incremental diff; the harness doesn't drive the
    /// reviewer loop, so empty like the PR diff above.</summary>
    public override Task<string> GetCompareDiffAsync(
        string baseSha, string headSha, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public override Task<IReadOnlyList<PrComment>> GetIssueCommentsAsync(
        int prNumber, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PrComment>>(Array.Empty<PrComment>());

    public override Task<IReadOnlyList<PrCommit>> GetCompareCommitsAsync(
        string baseSha, string headSha, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PrCommit>>(Array.Empty<PrCommit>());
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
    internal ConcurrentDictionary<(int Number, string Marker), PublishedReview> ReviewReceipts { get; } = new();
    internal ConcurrentDictionary<int, bool> HumanReviewLabels { get; } = new();
    private readonly ConcurrentDictionary<int, PullRequest> _prs = new();
    private readonly ConcurrentDictionary<string, string> _branchShas = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _branchShaWaiters = new();
    private readonly ConcurrentDictionary<string, CommitState> _commitStates = new();
    private readonly ConcurrentDictionary<int, (string HeadSha, DateTimeOffset MergedAt)> _merges = new();
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
        if (!PrInfo.TryGetValue(prNumber, out var info))
            throw new InvalidOperationException($"PR #{prNumber} not found");
        var head = GetBranchSha(info.HeadBranch) ?? info.HeadSha;
        _merges.TryAdd(prNumber, (head, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Test query — returns true if the PR was merged via
    /// <see cref="LocalGitHubService.MergePullRequestAsync"/>.
    /// </summary>
    public bool WasMerged(int prNumber)
        => _merges.ContainsKey(prNumber);

    public void RegisterPushedBranch(string branch, string sha)
    {
        _branchShas[branch] = sha;
        if (_branchShaWaiters.TryRemove(branch, out var tcs))
        {
            tcs.TrySetResult(sha);
        }
    }

    public string? GetBranchSha(string branch)
        => _branchShas.TryGetValue(branch, out var s) ? s : null;

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
        if (!_prs.ContainsKey(number) || !PrInfo.TryGetValue(number, out var info))
            throw new InvalidOperationException($"PR #{number} not found");
        var merged = _merges.TryGetValue(number, out var merge);
        var head = merged ? merge.HeadSha : GetBranchSha(info.HeadBranch) ?? info.HeadSha;
        // Octokit models have nonpublic setters. Populate a fresh fake API response;
        // previously the side-channel merge flag never appeared in this observation.
        var pr = new PullRequest(number);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Title))!.SetValue(pr, info.Title);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Body))!.SetValue(pr, info.Body);
        typeof(PullRequest).GetProperty(nameof(PullRequest.Head))!.SetValue(pr,
            new GitReference(null!, null!, null!, info.HeadBranch, head, null!, null!));
        typeof(PullRequest).GetProperty(nameof(PullRequest.Base))!.SetValue(pr,
            new GitReference(null!, null!, null!, info.BaseBranch, GetBranchSha(info.BaseBranch) ?? "", null!, null!));
        if (merged)
        {
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergedAt))!.SetValue(pr, merge.MergedAt);
            typeof(PullRequest).GetProperty(nameof(PullRequest.MergeCommitSha))!.SetValue(pr, merge.HeadSha);
        }
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
