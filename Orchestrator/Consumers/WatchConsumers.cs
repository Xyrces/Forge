using Forge.Core.Messaging;
using Forge.Messaging;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>
/// Shared resolution for watch consumers: the event is a HINT — re-read
/// the task from the owning project's store (DB truth) before acting.
/// </summary>
public abstract class WatchConsumerBase<T> : EventConsumer<T> where T : IForgeEvent
{
    private readonly IProjectDispatchBundleFactory _bundleFactory;
    private readonly Core.IProjectStore _projectStore;

    protected WatchConsumerBase(
        ITransport transport,
        IProjectDispatchBundleFactory bundleFactory,
        Core.IProjectStore projectStore,
        ILogger logger)
        : base(transport, logger)
    {
        _bundleFactory = bundleFactory;
        _projectStore = projectStore;
    }

    protected async Task<ProjectDispatchBundle?> BundleForAsync(
        string projectId, ILogger logger, CancellationToken ct)
    {
        var records = await _projectStore.ListAsync(ct);
        var record = records
            .FirstOrDefault(r => string.Equals(r.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            logger.LogWarning("{Consumer}: unknown project {ProjectId} — hint dropped (backstop covers)", GetType().Name, projectId);
            return null;
        }
        try
        {
            return _bundleFactory.Build(new Configuration.ProjectOptions
            {
                Id = record.Id,
                Name = record.Name,
                RepoUrl = record.RepoUrl,
                DefaultBranch = record.DefaultBranch,
                Root = string.Empty,
                Roles = new Dictionary<string, int>(record.Roles, StringComparer.Ordinal),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Consumer}: bundle build failed for project {ProjectId}", GetType().Name, projectId);
            return null;
        }
    }
}

/// <summary>
/// PrOpened → launch the background review immediately (the reviewer
/// starts on the pushed head while CI runs; the sweep is the backstop).
/// The handler only kicks — the review runs off-loop.
/// </summary>
public sealed class PrOpenedConsumer : WatchConsumerBase<PrOpened>
{
    private readonly WatchSweepService _sweeps;
    private readonly ILogger<PrOpenedConsumer> _logger;

    public PrOpenedConsumer(
        ITransport transport,
        IProjectDispatchBundleFactory bundleFactory,
        Core.IProjectStore projectStore,
        WatchSweepService sweeps,
        ILogger<PrOpenedConsumer> logger)
        : base(transport, bundleFactory, projectStore, logger)
    {
        _sweeps = sweeps;
        _logger = logger;
    }

    protected override async Task HandleAsync(PrOpened evt, CancellationToken ct)
    {
        var bundle = await BundleForAsync(evt.ProjectId, _logger, ct);
        if (bundle is null) return;
        var task = await bundle.IssueStore.GetAsync(evt.TaskId, ct);
        if (task is null || task.GetMetadata("prNumber") is null)
        {
            _logger.LogDebug("PrOpened hint for {TaskId} is stale (no task/prNumber) — sweep backstop covers", evt.TaskId);
            return;
        }
        await _sweeps.TryLaunchBackgroundReviewAsync(task, bundle, ct);
    }
}

/// <summary>
/// ReviewVerdictRecorded → attempt the merge/rework decision NOW
/// (verdict + CI arrive together; no sweep-interval dwell).
/// </summary>
public sealed class ReviewVerdictRecordedConsumer : WatchConsumerBase<ReviewVerdictRecorded>
{
    private readonly ILogger<ReviewVerdictRecordedConsumer> _logger;

    public ReviewVerdictRecordedConsumer(
        ITransport transport,
        IProjectDispatchBundleFactory bundleFactory,
        Core.IProjectStore projectStore,
        ILogger<ReviewVerdictRecordedConsumer> logger)
        : base(transport, bundleFactory, projectStore, logger)
    {
        _logger = logger;
    }

    protected override async Task HandleAsync(ReviewVerdictRecorded evt, CancellationToken ct)
    {
        var bundle = await BundleForAsync(evt.ProjectId, _logger, ct);
        if (bundle is null) return;
        var task = await bundle.IssueStore.GetAsync(evt.TaskId, ct);
        if (task is null || task.GetMetadata("prNumber") is null) return;
        try
        {
            var outcome = await bundle.PrWatcher.PollWatchedTaskAsync(task, ct);
            _logger.LogInformation("Verdict-driven poll for {TaskId}: {Outcome}", evt.TaskId, outcome);
        }
        catch (Octokit.RateLimitExceededException)
        {
            _logger.LogWarning("Verdict-driven poll for {TaskId} hit the GitHub rate limit — sweep backstop covers", evt.TaskId);
        }
    }
}

