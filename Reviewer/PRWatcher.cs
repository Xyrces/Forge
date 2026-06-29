using Microsoft.Extensions.Logging;
using Octokit;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using static Octokit.CommitState;
using static Octokit.PullRequestReviewState;

namespace PortHorizon.Agents.Reviewer;

public sealed class PRWatcher
{
    private readonly GitHubService _gitHub;
    private readonly GitWorktreeService _worktrees;
    private readonly StateStore _state;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _staleAfter;
    private readonly ILogger<PRWatcher> _logger;
    private readonly IDashboardEventBus _events;

    public PRWatcher(
        GitHubService gitHub,
        GitWorktreeService worktrees,
        StateStore state,
        TimeSpan pollInterval,
        TimeSpan staleAfter,
        IDashboardEventBus events,
        ILogger<PRWatcher> logger)
    {
        _gitHub = gitHub;
        _worktrees = worktrees;
        _state = state;
        _pollInterval = pollInterval;
        _staleAfter = staleAfter;
        _events = events;
        _logger = logger;
    }

    public async Task<int> ProcessWatchTaskAsync(AgentTask watchTask, CancellationToken cancellationToken)
    {
        if (!int.TryParse(watchTask.Parameters.GetValueOrDefault("prNumber")?.ToString(), out var prNumber))
        {
            _logger.LogError("Watch task {TaskId} missing prNumber", watchTask.Id);
            await MarkCompletedAsync(watchTask, error: "missing prNumber", cancellationToken);
            return 1;
        }

        var taskId = watchTask.Parameters.GetValueOrDefault("taskId")?.ToString() ?? watchTask.Id;
        var branch = watchTask.Parameters.GetValueOrDefault("branch")?.ToString() ?? watchTask.Branch;
        var worktreePath = watchTask.Parameters.GetValueOrDefault("worktreePath")?.ToString();

        var startedAt = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow - startedAt > _staleAfter)
            {
                _logger.LogWarning("PR #{PrNumber} timed out after {Minutes:F0} minutes", prNumber, _staleAfter.TotalMinutes);
                await MarkTaskAsync(taskId, AgentTaskStatus.Failed, "pr-stale", cancellationToken);
                await MarkCompletedAsync(watchTask, error: "pr-stale", cancellationToken);
                await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
                return 124;
            }

            var pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);
            var sha = pr.Head.Sha;
            var ci = await _gitHub.GetCommitStatusAsync(sha, cancellationToken);
            var reviews = await _gitHub.GetReviewsAsync(prNumber, cancellationToken);
            var verdict = EvaluateVerdict(ci, reviews);

            _logger.LogDebug("PR #{PrNumber}: CI={Ci}, ReviewVerdict={Verdict}", prNumber, ci, verdict);

            switch (verdict)
            {
                case ReviewVerdict.GreenAndApproved:
                    var merged = await _gitHub.MergePullRequestAsync(prNumber, cancellationToken);
                    if (merged)
                    {
                        await _gitHub.DeleteBranchAsync(branch, cancellationToken);
                        await MarkTaskAsync(taskId, AgentTaskStatus.Completed, null, cancellationToken);
                        await MarkCompletedAsync(watchTask, error: null, cancellationToken);
                        await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
                        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrMerged,
                            taskId, $"PR #{prNumber} merged and branch deleted"));
                        _logger.LogInformation("PR #{PrNumber} merged; task {TaskId} completed", prNumber, taskId);
                        return 0;
                    }
                    _logger.LogWarning("PR #{PrNumber} merge returned false; will retry next poll", prNumber);
                    break;

                case ReviewVerdict.GreenChangesRequested:
                    await MarkTaskAsync(taskId, AgentTaskStatus.Blocked,
                        "Reviewer requested changes", cancellationToken);
                    await MarkCompletedAsync(watchTask, error: "changes-requested", cancellationToken);
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrChangesRequested,
                        taskId, $"PR #{prNumber} marked Blocked (changes requested)"));
                    _logger.LogInformation("PR #{PrNumber} marked Blocked (changes requested)", prNumber);
                    return 0;

                case ReviewVerdict.CiFailed:
                    await MarkTaskAsync(taskId, AgentTaskStatus.Failed,
                        $"CI failed for {sha}: {ci}", cancellationToken);
                    await MarkCompletedAsync(watchTask, error: "ci-failed", cancellationToken);
                    await TryRemoveWorktreeAsync(worktreePath, cancellationToken);
                    _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrFailed,
                        taskId, $"PR #{prNumber} CI failed ({ci})"));
                    _logger.LogInformation("PR #{PrNumber} CI failed; task {TaskId} failed", prNumber, taskId);
                    return 1;

                case ReviewVerdict.Pending:
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

    private async Task MarkTaskAsync(string taskId, AgentTaskStatus status, string? error, CancellationToken ct)
    {
        var state = await _state.LoadStateAsync(ct);
        var idx = state.Tasks.FindIndex(t => t.Id == taskId);
        if (idx < 0) return;
        var task = state.Tasks[idx];
        var newTask = (status, task.CompletedAt) switch
        {
            (AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Blocked, _) =>
                task with { Status = status, Error = error, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow },
            _ => task with { Status = status, Error = error, UpdatedAt = DateTime.UtcNow },
        };
        state.Tasks[idx] = newTask;
        if (status == AgentTaskStatus.Completed) state.CompletedTasks++;
        if (status == AgentTaskStatus.Failed) state.FailedTasks++;
        await _state.SaveStateAsync(state, ct);
    }

    private async Task MarkCompletedAsync(AgentTask watchTask, string? error, CancellationToken ct)
    {
        var state = await _state.LoadStateAsync(ct);
        var idx = state.Tasks.FindIndex(t => t.Id == watchTask.Id);
        if (idx < 0) return;
        state.Tasks[idx] = watchTask with
        {
            Status = error is null ? AgentTaskStatus.Completed : AgentTaskStatus.Failed,
            Error = error,
            UpdatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        await _state.SaveStateAsync(state, ct);
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
