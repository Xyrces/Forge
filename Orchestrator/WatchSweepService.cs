using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator;

/// <summary>
/// The state-driven PR watch sweep, extracted from OrchestratorAgent
/// for the message-driven watch pipeline: the SweepTick(watch)
/// consumer runs <see cref="SweepProjectAsync"/> (15m backstop,
/// GitHub truth), and the PrOpened consumer calls
/// <see cref="TryLaunchBackgroundReviewAsync"/> for the immediate
/// review fast path. Owns the in-flight review registry and the
/// GitHub 429 cooldown that used to live on the orchestrator.
/// </summary>
public sealed class WatchSweepService
{
    private readonly IAgentRunner _runner;
    private readonly LlmConfig? _llmConfig;
    private readonly RoleModelOverrides? _modelOverrides;
    private readonly ModelRateLimitTracker _modelCooldowns;
    private readonly TaskStateMachine? _lifecycle;
    private readonly Core.Workflow.WorkflowResolver? _workflow;
    private readonly IDashboardEventBus _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatchSweepService> _logger;

    private DateTime _githubRateLimitedUntil = DateTime.MinValue;
    private static readonly TimeSpan GitHubRateLimitCooldown = TimeSpan.FromMinutes(10);

    private const int MaxAutoResumeAttempts = Forge.Reviewer.PRWatcher.MaxAutoResumeAttempts;

    /// <summary>Reviews launched off-loop, keyed project/task. A review
    /// that outlives <see cref="ReviewRelaunchAfter"/> without landing a
    /// verdict (crashed silently, process restarted mid-review) becomes
    /// eligible for relaunch — the dispatcher's own ReviewRunTimeout is
    /// the inner bound.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _reviewsInFlight = new();
    private static readonly TimeSpan ReviewRelaunchAfter = TimeSpan.FromMinutes(15);

    public WatchSweepService(
        IAgentRunner runner,
        LlmConfig? llmConfig,
        RoleModelOverrides? modelOverrides,
        ModelRateLimitTracker modelCooldowns,
        TaskStateMachine? lifecycle,
        Core.Workflow.WorkflowResolver? workflow,
        IDashboardEventBus events,
        ILoggerFactory loggerFactory,
        ILogger<WatchSweepService> logger)
    {
        _runner = runner;
        _llmConfig = llmConfig;
        _modelOverrides = modelOverrides;
        _modelCooldowns = modelCooldowns;
        _lifecycle = lifecycle;
        _workflow = workflow;
        _events = events;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Watched-task discovery: any live task (Pending|InProgress, or
    /// Blocked with a PR) carrying a prNumber IS the watch (watch rows
    /// were retired 2026-07-29). Legacy pr-watch rows still in the
    /// queue are Closed as superseded — the metadata lives on the task.
    /// </summary>
    internal async Task<IReadOnlyList<IssueRecord>> DiscoverWatchedTasksAsync(
        ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        var all = await bundle.IssueStore.ListAsync(new IssueFilter(), cancellationToken);
        var legacyWatches = all.Where(i => i.Type == AgentTaskTypes.PrWatch
            && i.Status is IssueStatus.Pending or IssueStatus.InProgress).ToList();
        foreach (var legacy in legacyWatches)
        {
            await bundle.IssueStore.TransitionAsync(legacy.Id, IssueStatus.Closed,
                "superseded: PR watching is driven by the watched task's own state (prNumber metadata) — no watch row needed",
                ct: cancellationToken);
            _logger.LogInformation("Closed legacy watch {Id} (superseded by state-driven watching)", legacy.Id);
        }
        return all.Where(t => !AgentTaskTypes.IsContainer(t.Type)
                && t.Type != AgentTaskTypes.PrWatch
                && (t.Status is IssueStatus.Pending or IssueStatus.InProgress
                    || (t.Status == IssueStatus.Blocked && t.GetMetadata("prNumber") is not null)
                    // Failed-with-PR stays in the sweep for EXTERNAL-
                    // MERGE detection only (the poll short-circuits
                    // before CI/review/rework) — observed live
                    // 2026-08-13: porthorizon task-408's PR #940 merged
                    // after the breaker tripped; the Failed task was
                    // invisible to the sweep and never resolved.
                    || (t.Status == IssueStatus.Failed && t.GetMetadata("prNumber") is not null))
                && t.GetMetadata("prNumber") is not null)
            .ToList();
    }

    /// <summary>
    /// One sequential poll over every watched task in a project — a
    /// single GitHub burst per sweep instead of unbounded parallel poll
    /// loops. Review first (background, verdict metadata on the task),
    /// then the merge/rework decision (PRWatcher.PollWatchedTaskAsync).
    /// A 429 aborts the sweep early and arms the cooldown.
    /// </summary>
    public async Task SweepProjectAsync(ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        var watchedTasks = await DiscoverWatchedTasksAsync(bundle, cancellationToken);
        if (watchedTasks.Count == 0) return;
        if (DateTime.UtcNow < _githubRateLimitedUntil)
        {
            _logger.LogDebug("Watch sweep: skipping {N} watched tasks — GitHub rate-limit cooldown until {Until:HH:mm:ss}",
                watchedTasks.Count, _githubRateLimitedUntil);
            return;
        }

        _logger.LogInformation("Watch sweep: polling {N} watched task(s) (project={Project})",
            watchedTasks.Count, bundle.Project.Id);
        foreach (var watched in watchedTasks)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                // Blocked watch recovery (unblock nudge): transient
                // reviewer-unavailable blocks resume when the model is
                // back; ANY other Blocked task gets the mergeable gate
                // check — if the blockage cleared externally, the watch
                // resumes and merges without an operator roundtrip.
                var polled = watched;
                if (watched.Status == IssueStatus.Blocked)
                {
                    IssueRecord? resumed = IsAutoResumableBlock(watched)
                        ? await TryResumeBlockedWatchAsync(watched, bundle, cancellationToken)
                        : await bundle.PrWatcher.TryResumeMergeableBlockedAsync(watched, cancellationToken);
                    if (resumed is null) continue;
                    polled = resumed;
                }
                // Review OFF THE LOOP: an agentic review takes minutes;
                // awaiting it here would stall the sweep. The review
                // records its verdict in task metadata; the verdict
                // event (or the next sweep's poll) merges on it.
                // Failed tasks skip: they're in the sweep ONLY for
                // external-merge detection (breaker-tripped work waits
                // on the operator, not on more reviews).
                if (polled.Status is not IssueStatus.Failed)
                    await TryLaunchBackgroundReviewAsync(polled, bundle, cancellationToken);
                var poll = await bundle.PrWatcher.PollWatchedTaskAsync(polled, cancellationToken);
                _logger.LogDebug("Watch (task {Id}): {Outcome}", watched.Id, poll);
            }
            catch (Octokit.RateLimitExceededException)
            {
                _githubRateLimitedUntil = DateTime.UtcNow + GitHubRateLimitCooldown;
                _logger.LogWarning("Watch sweep: GitHub rate limit exceeded; backing off for {Cooldown} (project={Project})",
                    GitHubRateLimitCooldown, bundle.Project.Id);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watched task {Id} crashed (project={Project})", watched.Id, bundle.Project.Id);
            }
            // Courtesy delay: GitHub's secondary rate limit dislikes
            // rapid-fire request bursts even well under the quota.
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static bool IsAutoResumableBlock(IssueRecord task) =>
        task.Status == IssueStatus.Blocked
        && string.Equals(task.GetMetadata("blockedKind"),
            Forge.Reviewer.PRWatcher.BlockedKindReviewerUnavailable, StringComparison.Ordinal);

    /// <summary>
    /// Auto-resume a transiently-blocked watch (reviewer-unavailable):
    /// clear the stale verdict, transition Blocked -> InProgress, and
    /// hand the task back to the sweep. Null when it should stay
    /// blocked (reviewer model still cooling, or budget exhausted —
    /// then the marker clears and the block becomes operator-decision).
    /// </summary>
    private async Task<IssueRecord?> TryResumeBlockedWatchAsync(
        IssueRecord task, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        var mk = ResolveModelKey("review", bundle.Project.Id);
        var cooling = _modelCooldowns.CoolingDownUntil(mk.Provider, mk.Model);
        if (cooling is not null)
        {
            _logger.LogDebug(
                "Watch (task {Id}): auto-resume deferred — reviewer model {Provider}/{Model} cooling until {Until:HH:mm:ss}",
                task.Id, mk.Provider, mk.Model, cooling.Value);
            return null;
        }

        var attempts = int.TryParse(task.GetMetadata("autoResumeAttempts"), out var a) ? a : 0;
        if (attempts >= MaxAutoResumeAttempts)
        {
            _logger.LogWarning(
                "Watch (task {Id}): auto-resume budget ({Max}) exhausted — clearing the transient marker; operator review required",
                task.Id, MaxAutoResumeAttempts);
            await bundle.IssueStore.TransitionAsync(task.Id, IssueStatus.Blocked,
                "auto-resume budget exhausted — operator review required",
                new Dictionary<string, object> { ["blockedKind"] = null! }, ct: cancellationToken);
            return null;
        }

        var metadata = new Dictionary<string, object>
        {
            ["blockedKind"] = null!,
            ["reviewVerdict"] = null!,
            ["reviewSha"] = null!,
            ["reviewNotes"] = null!,
            ["reviewRound"] = null!,
            ["lastError"] = null!,
            ["lastErrorAt"] = null!,
            ["autoResumeAttempts"] = (attempts + 1).ToString(),
            // The resume is a nudge — restart the stale window or an
            // hours-old PR trips the pr-stale guard on the first poll
            // after resume.
            ["prOpenedAt"] = DateTime.UtcNow.ToString("O"),
        };
        var resumed = await bundle.IssueStore.TransitionAsync(task.Id, IssueStatus.InProgress,
            $"auto-resumed (round {attempts + 1}/{MaxAutoResumeAttempts}): reviewer model available again — re-reviewing the PR head",
            metadata, ct: cancellationToken);
        await ReportLifecycleAsync(resumed, Core.TaskEvent.WatchResumed, bundle, cancellationToken);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
            task.Id, $"Watch auto-resumed (round {attempts + 1}/{MaxAutoResumeAttempts}): reviewer model available again"));
        _logger.LogInformation(
            "Watch (task {Id}): auto-resumed from transient reviewer-unavailable block (round {N}/{Max}, project={Project})",
            task.Id, attempts + 1, MaxAutoResumeAttempts, bundle.Project.Id);
        return resumed;
    }

    /// <summary>Launch decision, separated for tests: skip when a
    /// review is in flight (in-memory) or the task's reviewStartedAt
    /// marker is fresh (covers pre-restart reviews).</summary>
    internal bool ShouldLaunchReview(IssueRecord task, string projectId)
    {
        var key = projectId + "/" + task.Id;
        if (_reviewsInFlight.TryGetValue(key, out var inFlight) && !inFlight.IsCompleted)
        {
            return false;
        }
        if (DateTime.TryParse(task.GetMetadata("reviewStartedAt"), out var started)
            && DateTime.UtcNow - started.ToUniversalTime() < ReviewRelaunchAfter)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Launch the reviewer for a watched task in the background when
    /// the workflow's review step is enabled. The review records its
    /// own verdict metadata (which publishes ReviewVerdictRecorded —
    /// the merge attempt rides that event); the caller never awaits.
    /// </summary>
    public async Task TryLaunchBackgroundReviewAsync(
        IssueRecord task, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        var reviewEnabled = _workflow is null
            || Core.Workflow.WorkflowExtensions.IsStepEnabled(
                await _workflow.ResolveAsync(cancellationToken), "review");
        if (!reviewEnabled) return;

        var key = bundle.Project.Id + "/" + task.Id;
        if (!ShouldLaunchReview(task, bundle.Project.Id))
        {
            return;
        }

        var reviewer = new Forge.Reviewer.ReviewerDispatcher(
            bundle.IssueStore, bundle.GitHub, _runner,
            _loggerFactory.CreateLogger<Forge.Reviewer.ReviewerDispatcher>(),
            lifecycle: _lifecycle,
            events: _events,
            projectId: bundle.Project.Id,
            eventPublisher: bundle.IssueStore.Events);
        var run = reviewer.ReviewOnceAsync(task, cancellationToken);
        _reviewsInFlight[key] = run;
        _ = run.ContinueWith(t =>
        {
            _reviewsInFlight.TryRemove(key, out _);
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "background review for {TaskId} faulted (project={Project})", task.Id, bundle.Project.Id);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _logger.LogInformation("Watch (task {Id}): review launched in background (project={Project})", task.Id, bundle.Project.Id);
    }

    private async Task ReportLifecycleAsync(
        IssueRecord task, Core.TaskEvent evt, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        if (_lifecycle is null) return;
        try
        {
            await _lifecycle.ReportAsync(bundle.IssueStore, task, evt,
                watch: null, hasActiveDevRun: false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lifecycle report {Event} failed for {Id} (project={Project})",
                evt, task.Id, bundle.Project.Id);
        }
    }

    private (string Provider, string Model) ResolveModelKey(string taskType, string? projectId)
    {
        if (_llmConfig is null) return ("default", "default");
        try
        {
            var (provider, model, _) = _llmConfig.ResolveEffective(
                RoleAgentRegistry.FromTaskType(taskType), _modelOverrides, projectId);
            return (provider.Name, model);
        }
        catch (InvalidOperationException)
        {
            return ("default", "default");
        }
    }
}
