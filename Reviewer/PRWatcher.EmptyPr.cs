using Forge.Core;
using Forge.Dashboard;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Forge.Reviewer;

public sealed partial class PRWatcher
{
    // Do not stripe these gates: a busy unrelated task would discard a valid
    // event with WaitAsync(0), losing counter progress and adding reconciliation latency.
    // Callers retain strong references through Release, including policy batches.
    // Weak cache entries can therefore expire only after holders and waiters are
    // gone; never remove a live gate or dispose a semaphore another caller owns.
    private static readonly Dictionary<string, WeakReference<SemaphoreSlim>> PollGates = new(StringComparer.Ordinal);
    private static readonly object PollGateCacheLock = new();
    private static int PollGateCacheReads;
    private static string PollGateKey(string? projectId, string taskId) => $"{projectId}/{taskId}";
    private static SemaphoreSlim GetPollGateByKey(string key)
    {
        lock (PollGateCacheLock)
        {
            if (++PollGateCacheReads % 256 == 0)
                foreach (var stale in PollGates.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
                    PollGates.Remove(stale);
            if (PollGates.TryGetValue(key, out var weak) && weak.TryGetTarget(out var existing)) return existing;
            var created = new SemaphoreSlim(1, 1);
            PollGates[key] = new(created);
            return created;
        }
    }
    internal static SemaphoreSlim GetPollGate(string? projectId, string taskId) => GetPollGateByKey(PollGateKey(projectId, taskId));
    internal static IReadOnlyList<SemaphoreSlim> GetPollGatesForTasks(string? projectId, IEnumerable<string> taskIds)
        => taskIds.Select(id => PollGateKey(projectId, id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(GetPollGateByKey).ToArray();
    private sealed class LocalPollScope { public string Id { get; } = Guid.NewGuid().ToString("N"); }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IIssueStore, LocalPollScope> LocalPollScopes = new();
    private SemaphoreSlim PollGate(string taskId) => GetPollGate(_projectId ?? LocalPollScopes.GetOrCreateValue(_issues).Id, taskId);

    public async Task<WatchPollOutcome> PollWatchedTaskAsync(IssueRecord task, CancellationToken cancellationToken = default,
        Func<int, IReadOnlyList<PullRequestReviewState>>? reviewsOverride = null,
        Func<PullRequest, string>? headShaOverride = null, Func<PullRequest, bool?>? mergeableOverride = null,
        Func<PullRequest, int>? changedFilesOverride = null)
    {
        var gate = PollGate(task.Id);
        if (!await gate.WaitAsync(0, cancellationToken)) return WatchPollOutcome.Pending;
        try
        {
            var current = await _issues.GetAsync(task.Id, cancellationToken);
            if (current is null || current.GetMetadata("prNumber") != task.GetMetadata("prNumber"))
                return WatchPollOutcome.Pending;
            task = current;
            return await PollWatchedTaskCoreAsync(task, cancellationToken, reviewsOverride, headShaOverride, mergeableOverride, changedFilesOverride);
        }
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
            // An empty remote diff can be a transient observation while its rightful
            // worker is repairing the same branch. Do not reinterpret scope, block the
            // task, or join historical attempts until current ownership is absent.
            if (await EmptyPrHasWorkerOwnerAsync(task, ct)) return WatchPollOutcome.Pending;
            if (_joinTaskWorkers is null || !await _joinTaskWorkers(task.Id, ct)) return WatchPollOutcome.Pending;
            if (await EmptyPrHasWorkerOwnerAsync(task, ct)) return WatchPollOutcome.Pending;
            if (_issues is not IssueStore concrete || !await concrete.CanVerifyEmptyPrAsync(task.Id, ct))
            {
                if (await EmptyPrHasWorkerOwnerAsync(task, ct)) return WatchPollOutcome.Pending;
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
            var latest = await _gitHub.GetPullRequestAsync(number, ct);
            if (!SameEmptyPr(observed, latest, task)) return WatchPollOutcome.Pending;
            if (await EmptyPrHasWorkerOwnerAsync(task, ct)) return WatchPollOutcome.Pending;
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

    private async Task<bool> EmptyPrHasWorkerOwnerAsync(IssueRecord observedTask, CancellationToken ct)
    {
        var task = await _issues.GetAsync(observedTask.Id, ct);
        if (task is null || task.GetMetadata("prNumber") != observedTask.GetMetadata("prNumber")
            || task.GetMetadata("branch") != observedTask.GetMetadata("branch")
            || task.Status is not (IssueStatus.InProgress or IssueStatus.Blocked)) return true;
        var branch = task.GetMetadata("branch");
        var owners = (await _issues.ListAsync(new Forge.Core.IssueFilter(), ct))
            .Where(candidate => candidate.Id == task.Id || (!string.IsNullOrWhiteSpace(branch)
                && candidate.GetMetadata("branch") == branch)).ToList();
        var ownerIds = owners.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        if (_runs is not null && (await _runs.ListActiveAsync(ct)).Any(run => run.TaskId is not null
            && ownerIds.Contains(run.TaskId) && (_projectId is null || run.ProjectId is null || run.ProjectId == _projectId)))
            return true;
        foreach (var owner in owners)
        {
            if (_hasDurableWorker is not null && await _hasDurableWorker(owner.Id, ct)) return true;
            if (owner.Status == IssueStatus.InProgress && owner.DispatchCheckpoint is null or < DispatchCheckpoint.PushDone)
                return true;
        }
        return false;
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
