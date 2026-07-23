using Microsoft.Extensions.Logging;
using Octokit;
using Forge.AgentTools;
using Forge.Core;
using Forge.Dashboard;
using static Octokit.CommitState;
using static Octokit.PullRequestReviewState;

namespace Forge.Reviewer;

public sealed class PRWatcher
{
    private readonly GitHubService _gitHub;
    private readonly GitWorktreeService _worktrees;
    private readonly IIssueStore _issues;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleAfter;
    private readonly ILogger<PRWatcher> _logger;
    private readonly IDashboardEventBus _events;

    public PRWatcher(
        GitHubService gitHub,
        GitWorktreeService worktrees,
        IIssueStore issues,
        TimeSpan pollInterval,
        TimeSpan staleAfter,
        IDashboardEventBus events,
        ILogger<PRWatcher> logger)
    {
        _gitHub = gitHub;
        _worktrees = worktrees;
        _issues = issues;
        _pollInterval = pollInterval;
        _staleAfter = staleAfter;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Outcome of a single watch poll (<see cref="PollWatchOnceAsync"/>).
    /// </summary>
    public enum WatchPollOutcome
    {
        /// <summary>Nothing terminal; the watch stays Pending for the next sweep.</summary>
        Pending,
        /// <summary>PR was merged (externally or by us); task + watch completed.</summary>
        Merged,
        /// <summary>Reviewer requested changes; task + watch blocked.</summary>
        Blocked,
        /// <summary>CI failed; task + watch failed.</summary>
        CiFailed,
        /// <summary>Watch exceeded its stale window; task + watch failed.</summary>
        Stale,
        /// <summary>Unrecoverable input (missing prNumber); watch failed.</summary>
        Error,
    }

    /// <summary>
    /// ONE poll of a watched PR: fetch state, evaluate CI + reviews,
    /// and apply any terminal transition (merged / changes-requested /
    /// CI-failed / stale). Returns without retrying when nothing is
    /// terminal yet — the caller (OrchestratorAgent's watch sweep)
    /// re-polls on its own slow cadence. This is the quota-friendly
    /// path: 3 API calls per watch per sweep, no internal loop. The
    /// stale window is anchored to the watch issue's CreatedAt, so it
    /// stays stable no matter which sweep picks the watch up.
    /// </summary>
    public async Task<WatchPollOutcome> PollWatchOnceAsync(
        IssueRecord watchTask,
        CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null)
    {
        var prText = watchTask.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            _logger.LogError("Watch issue {Id} missing prNumber", watchTask.Id);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "missing prNumber", ct: cancellationToken);
            return WatchPollOutcome.Error;
        }

        var taskId = watchTask.GetMetadata("taskId") ?? watchTask.Id;
        var branch = watchTask.GetMetadata("branch") ?? watchTask.Title;
        var worktreePath = watchTask.GetMetadata("worktreePath");

        if (DateTime.UtcNow - watchTask.CreatedAt > _staleAfter)
        {
            _logger.LogWarning("PR #{PrNumber} timed out after {Minutes:F0} minutes", prNumber, _staleAfter.TotalMinutes);
            await _issues.TransitionAsync(taskId, IssueStatus.Failed, "pr-stale", ct: cancellationToken);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "pr-stale", ct: cancellationToken);
            await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
            return WatchPollOutcome.Stale;
        }

        var pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);

        // Externally merged (operator merged by hand, or the
        // branch protection bot did): close the loop exactly as
        // if we had merged it ourselves. Without this check the
        // watch polls forever on CI+reviews of a dead PR.
        if (pr.Merged)
        {
            _logger.LogInformation("PR #{PrNumber} was merged externally; closing task {TaskId}", prNumber, taskId);
            await _gitHub.DeleteBranchAsync(branch, cancellationToken);
            await _issues.TransitionAsync(taskId, IssueStatus.Completed, null, ct: cancellationToken);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Completed, null, ct: cancellationToken);
            await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrMerged,
                taskId, $"PR #{prNumber} merged externally; task completed"));
            return WatchPollOutcome.Merged;
        }

        // P4 e2e-harness seam: tests can return the SHA
        // directly without going through Octokit's
        // PullRequest.Head (which has a private setter).
        var sha = headShaOverride is not null ? headShaOverride(pr) : pr.Head.Sha;
        var ci = await _gitHub.GetCommitStatusAsync(sha, cancellationToken);
        // P4 e2e-harness seam: tests can pre-approve reviews
        // without going through Octokit's sealed
        // PullRequestReview ctor. Default = real GitHub call.
        var reviewStates = reviewsOverride is not null
            ? reviewsOverride(prNumber)
            : (await _gitHub.GetReviewsAsync(prNumber, cancellationToken))
                .Select(r => r.State.Value).ToList();
        var verdict = EvaluateVerdictFromStates(ci, reviewStates);

        _logger.LogDebug("PR #{PrNumber}: CI={Ci}, ReviewVerdict={Verdict}", prNumber, ci, verdict);

        switch (verdict)
        {
            case ReviewVerdict.GreenAndApproved:
                var merged = await _gitHub.MergePullRequestAsync(prNumber, cancellationToken);
                if (merged)
                {
                    await _gitHub.DeleteBranchAsync(branch, cancellationToken);
                    await _issues.TransitionAsync(taskId, IssueStatus.Completed, null, ct: cancellationToken);
                    await _issues.TransitionAsync(watchTask.Id, IssueStatus.Completed, null, ct: cancellationToken);
                    await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrMerged,
                        taskId, $"PR #{prNumber} merged and branch deleted"));
                    _logger.LogInformation("PR #{PrNumber} merged; task {TaskId} completed", prNumber, taskId);
                    return WatchPollOutcome.Merged;
                }
                _logger.LogWarning("PR #{PrNumber} merge returned false; will retry next poll", prNumber);
                return WatchPollOutcome.Pending;

            case ReviewVerdict.GreenChangesRequested:
                await _issues.TransitionAsync(taskId, IssueStatus.Blocked, "Reviewer requested changes", ct: cancellationToken);
                await _issues.TransitionAsync(watchTask.Id, IssueStatus.Blocked, "changes-requested", ct: cancellationToken);
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrChangesRequested,
                    taskId, $"PR #{prNumber} marked Blocked (changes requested)"));
                _logger.LogInformation("PR #{PrNumber} marked Blocked (changes requested)", prNumber);
                return WatchPollOutcome.Blocked;

            case ReviewVerdict.CiFailed:
                await _issues.TransitionAsync(taskId, IssueStatus.Failed, $"CI failed for {sha}: {ci}", ct: cancellationToken);
                await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "ci-failed", ct: cancellationToken);
                await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrFailed,
                    taskId, $"PR #{prNumber} CI failed ({ci})"));
                _logger.LogInformation("PR #{PrNumber} CI failed; task {TaskId} failed", prNumber, taskId);
                return WatchPollOutcome.CiFailed;

            case ReviewVerdict.Pending:
            default:
                return WatchPollOutcome.Pending;
        }
    }

    public async Task<int> ProcessWatchTaskAsync(
        IssueRecord watchTask,
        CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null)
    {
        var prText = watchTask.GetMetadata("prNumber");
        if (!int.TryParse(prText, out _))
        {
            _logger.LogError("Watch issue {Id} missing prNumber", watchTask.Id);
            await _issues.TransitionAsync(watchTask.Id, IssueStatus.Failed, "missing prNumber", ct: cancellationToken);
            return 1;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = await PollWatchOnceAsync(
                watchTask, cancellationToken, reviewsOverride, headShaOverride);
            switch (outcome)
            {
                case WatchPollOutcome.Merged:
                case WatchPollOutcome.Blocked:
                    return 0;
                case WatchPollOutcome.CiFailed:
                case WatchPollOutcome.Error:
                    return 1;
                case WatchPollOutcome.Stale:
                    return 124;
                case WatchPollOutcome.Pending:
                default:
                    break;
            }

            try { await Task.Delay(_pollInterval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        return 0;
    }

    public static ReviewVerdict EvaluateVerdict(CommitState ci, IReadOnlyList<PullRequestReview> reviews)
        => EvaluateVerdictFromStates(ci, reviews.Select(r => r.State.Value).ToList());

    public static ReviewVerdict EvaluateVerdictFromStates(CommitState ci, IReadOnlyCollection<PullRequestReviewState> reviewStates)
    {
        if (ci == CommitState.Failure || ci == CommitState.Error)
            return ReviewVerdict.CiFailed;

        var hasApproved = reviewStates.Any(s => s == Approved);
        var hasChanges = reviewStates.Any(s => s == ChangesRequested);

        return (ci, hasApproved, hasChanges) switch
        {
            (Success, true, _) => ReviewVerdict.GreenAndApproved,
            (Success, _, true) => ReviewVerdict.GreenChangesRequested,
            _ => ReviewVerdict.Pending
        };
    }

    private async Task TryRemoveWorktreeAsync(string? worktreePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(worktreePath)) return;
        try
        {
            var taskId = Path.GetFileName(worktreePath);
            await _worktrees.RemoveAsync(taskId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove worktree {Path}", worktreePath);
        }
    }
}

public enum ReviewVerdict
{
    Pending,
    GreenAndApproved,
    GreenChangesRequested,
    CiFailed
}
