using Forge.Core;
using Forge.Dashboard;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Forge.Reviewer;

public sealed partial class PRWatcher
{
    private static readonly SemaphoreSlim[] PollGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SemaphoreSlim PollGate(string taskId) => PollGates[(StringComparer.Ordinal.GetHashCode($"{_projectId}/{taskId}") & int.MaxValue) % PollGates.Length];

    public async Task<WatchPollOutcome> PollWatchedTaskAsync(IssueRecord task, CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null, Func<PullRequest, bool?>? mergeableOverride = null,
        Func<PullRequest, int>? changedFilesOverride = null)
    {
        var gate = PollGate(task.Id);
        if (!await gate.WaitAsync(0, cancellationToken)) return WatchPollOutcome.Pending;
        try { return await PollWatchedTaskCoreAsync(task, cancellationToken, reviewsOverride, headShaOverride, mergeableOverride, changedFilesOverride); }
        finally { gate.Release(); }
    }

    public async Task<IssueRecord?> TryResumeMergeableBlockedAsync(IssueRecord task, CancellationToken cancellationToken = default,
        Func<PullRequest, string>? headShaOverride = null, Func<PullRequest, bool?>? mergeableOverride = null,
        Func<int, IEnumerable<PullRequestReviewState>>? reviewsOverride = null)
    {
        var gate = PollGate(task.Id);
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            task = await _issues.GetAsync(task.Id, cancellationToken) ?? task;
            return await TryResumeMergeableBlockedCoreAsync(task, cancellationToken, headShaOverride, mergeableOverride, reviewsOverride);
        }
        finally { gate.Release(); }
    }

    private async Task<WatchPollOutcome> RetireEmptyPrForVerificationAsync(IssueRecord task, PullRequest observed, CancellationToken ct)
    {
        var gates = ReviewWorkerAttempt.RetirementGates(_projectId, task.Id);
        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var gate in gates)
            {
                if (!await gate.WaitAsync(0, ct)) return WatchPollOutcome.Pending;
                acquired.Add(gate);
            }
            task = await _issues.GetAsync(task.Id, ct) ?? task;
            if (!int.TryParse(task.GetMetadata("prNumber"), out var number) || number != observed.Number)
                return WatchPollOutcome.Pending;
            if (task.Status is not (IssueStatus.InProgress or IssueStatus.Blocked))
                return WatchPollOutcome.Pending;
            if (_issues is not IssueStore concrete || !await concrete.CanVerifyEmptyPrAsync(task.Id, ct))
            {
                const string reason = "Empty PR is not acceptance evidence. The committed scope requires a merged PR or an explicit approved existing-implementation criterion; inspect the scope before retrying.";
                if (task.Status != IssueStatus.Blocked)
                {
                    await ReportLifecycleAsync(task, TaskEvent.OperatorBlocked, ct);
                    await _issues.TransitionAsync(task.Id, IssueStatus.Blocked, reason, ct: ct);
                }
                return WatchPollOutcome.Blocked;
            }
            // False is not pod absence. This seam joins every historical role attempt;
            // unavailable state or unconfirmed termination leaves all artifacts untouched.
            if (task.Status == IssueStatus.InProgress && task.DispatchCheckpoint is null or < DispatchCheckpoint.PushDone)
                return WatchPollOutcome.Pending;
            if (_joinTaskWorkers is null || !await _joinTaskWorkers(task.Id, ct)) return WatchPollOutcome.Pending;
            if (_runs is not null && (await _runs.ListActiveAsync(ct)).Any(run => run.TaskId == task.Id))
                return WatchPollOutcome.Pending;
            var latest = await _gitHub.GetPullRequestAsync(number, ct);
            if (!SameEmptyPr(observed, latest, task)) return WatchPollOutcome.Pending;
            if (latest.State.Value == ItemState.Open) await _gitHub.ClosePullRequestAsync(number, ct);
            var closed = await _gitHub.GetPullRequestAsync(number, ct);
            if (closed.State.Value != ItemState.Closed || !SameEmptyPr(latest, closed, task)) return WatchPollOutcome.Pending;
            await concrete.RetireEmptyPrAsync(task, new(number, closed.Head.Sha, closed.Base.Sha,
                closed.Base.Ref, closed.Head.Ref, DateTimeOffset.UtcNow), ct);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition, task.Id,
                "Empty PR retired; fresh implementation verification queued. Delivery remains unproven."));
            return WatchPollOutcome.Reworking;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Task {Id}: empty PR retirement could not be proven; retaining the nonterminal watch", task.Id);
            return WatchPollOutcome.Pending;
        }
        finally
        {
            for (var index = acquired.Count - 1; index >= 0; index--) acquired[index].Release();
        }
    }

    private bool SameEmptyPr(PullRequest before, PullRequest after, IssueRecord task) =>
        !before.Merged && !after.Merged && before.Number == after.Number && after.ChangedFiles == 0
        && before.Head?.Sha is { Length: 40 } && before.Base?.Sha is { Length: 40 }
        && before.Head.Sha == after.Head?.Sha && before.Base.Sha == after.Base?.Sha
        && before.Head.Ref == after.Head.Ref && before.Base.Ref == after.Base.Ref
        && after.Head.Ref == task.GetMetadata("branch") && after.Base.Ref == _worktrees.DefaultBranch
        && !string.IsNullOrWhiteSpace(after.Base.Repository?.FullName)
        && string.Equals(after.Base.Repository.FullName, $"{_gitHub.Owner}/{_gitHub.Repo}", StringComparison.OrdinalIgnoreCase)
        && after.Base.Repository.FullName == after.Head.Repository?.FullName
        && before.Base.Repository?.FullName == after.Base.Repository.FullName;
}
