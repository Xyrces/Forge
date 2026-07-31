using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Forge.Meshy;

namespace Forge.Orchestrator;

/// <summary>
/// P2.b: scheduled Artist. Wakes up every <c>Interval</c> and
/// runs the Artist agent against any specs in
/// <c>SpecStatus.Designed</c> (plus re-runs of specs whose last
/// Artist run failed). Each run writes a row to <c>artist_run</c>
/// with the trigger kind set to <c>scheduled</c>.
///
/// <para>
/// Manual art via <c>POST /api/specs/{id}/design-art</c> writes
/// the same table with trigger=<c>manual</c>. The scheduler is
/// best-effort: it catches exceptions per-spec and continues; a
/// failing spec doesn't block the others.
/// </para>
///
/// <para>
/// Designed as a plain class (not a <c>BackgroundService</c>)
/// because the orchestrator doesn't use an
/// <see cref="Microsoft.Extensions.Hosting.IHost"/>. The
/// composition root in <c>Program.cs</c> launches it as a
/// fire-and-forget Task tied to the shutdown token.
/// </para>
/// </summary>
public sealed class ArtistScheduler
{
    private readonly ISpecStore _specs;
    private readonly ArtistAgentFactory _artistFactory;
    private readonly ArtistRunStore _runs;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<ArtistScheduler> _logger;
    private readonly TimeSpan _interval;

    public ArtistScheduler(
        ISpecStore specs,
        ArtistAgentFactory artistFactory,
        ArtistRunStore runs,
        IDashboardEventBus events,
        ILogger<ArtistScheduler> logger,
        TimeSpan? interval = null)
    {
        _specs = specs;
        _artistFactory = artistFactory;
        _runs = runs;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
    }

    public TimeSpan Interval => _interval;

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger the first tick so we don't fight the dashboard at boot.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await TickAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        IReadOnlyList<SpecRecord> candidates;
        try
        {
            candidates = await _specs.ListAsync(projectId: null, status: SpecStatus.Designed, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArtistScheduler: list specs failed; skipping tick");
            return;
        }

        foreach (var spec in candidates)
        {
            if (!await ShouldArtAsync(spec, ct)) continue;
            try
            {
                await RunOneAsync(spec, ArtistTriggerKind.Scheduled, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArtistScheduler: spec {Id} failed; continuing", spec.Id);
            }
        }
    }

    /// <summary>
    /// Heuristic: art a spec at most every <c>Interval</c>, or
    /// immediately if the last run failed. The recent-run lookup
    /// is <c>SELECT ts, status FROM artist_run WHERE spec_id =
    /// $id ORDER BY ts DESC LIMIT 1</c>.
    /// </summary>
    private async Task<bool> ShouldArtAsync(SpecRecord spec, CancellationToken ct)
    {
        var recent = await _runs.ListAsync(specId: spec.Id, limit: 1, ct);
        if (recent.Count == 0) return true;
        var last = recent[0];
        if (last.Status is ArtistRunStatus.MeshyFailed or ArtistRunStatus.LlmFailed) return true;
        return DateTime.UtcNow - last.Ts >= _interval;
    }

    private async Task RunOneAsync(SpecRecord spec, ArtistTriggerKind trigger, CancellationToken ct)
    {
        var agent = _artistFactory.Create();
        var result = await agent.ArtSpecAsync(spec.Id, trigger, ct);
        if (!result.Success)
        {
            _logger.LogWarning("ArtistScheduler: spec {Id} failed: {Err}", spec.Id, result.Error);
        }
        else
        {
            _logger.LogInformation("ArtistScheduler: spec {Id} -> {Status} (art={N}, meshy={M})",
                spec.Id, result.NewSpecStatus, result.ArtOutputIds.Count, result.MeshyTasks.Count);
        }
    }
}

/// <summary>
/// Factory for ArtistAgent instances. Each scheduled or manual
/// art run builds a fresh agent. Mirrors <c>DesignerAgentFactory</c>.
/// </summary>
public sealed class ArtistAgentFactory
{
    private readonly ISpecStore _specs;
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ArtOutputStore _artOutputs;
    private readonly ArtistRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly MeshyClient _meshy;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly IDashboardEventBus _events;
    private readonly ILoggerFactory _loggerFactory;

    public ArtistAgentFactory(
        ISpecStore specs,
        DesignArtifactStore designArtifacts,
        ArtOutputStore artOutputs,
        ArtistRunStore runs,
        MemoryStore memory,
        MeshyClient meshy,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ILoggerFactory loggerFactory)
    {
        _specs = specs;
        _designArtifacts = designArtifacts;
        _artOutputs = artOutputs;
        _runs = runs;
        _memory = memory;
        _meshy = meshy;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _loggerFactory = loggerFactory;
    }

    public ArtistAgent Create() => new(
        _specs, _designArtifacts, _artOutputs, _runs, _memory, _meshy, _chatClientFactory, _config, _roles, _events,
        _loggerFactory.CreateLogger<ArtistAgent>());
}
