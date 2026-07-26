using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator;

/// <summary>
/// P2.a: scheduled Designer. Wakes up every <c>Interval</c> and
/// runs the Designer agent against any specs in
/// <c>SpecStatus.ReadyForDesign</c> (plus re-runs of specs whose
/// last Designer run failed). Each run writes a row to
/// <c>designer_run</c> with the trigger kind set to
/// <c>scheduled</c>.
///
/// <para>
/// Manual design via <c>POST /api/specs/{id}/design</c> writes the
/// same table with trigger=<c>manual</c>. The scheduler is
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
public sealed class DesignerScheduler
{
    private readonly ISpecStore _specs;
    private readonly DesignerAgentFactory _designerFactory;
    private readonly DesignerRunStore _runs;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<DesignerScheduler> _logger;
    private readonly TimeSpan _interval;
    private readonly Core.StageGates? _gates;

    public DesignerScheduler(
        ISpecStore specs,
        DesignerAgentFactory designerFactory,
        DesignerRunStore runs,
        IDashboardEventBus events,
        ILogger<DesignerScheduler> logger,
        TimeSpan? interval = null,
        Core.StageGates? gates = null)
    {
        _specs = specs;
        _designerFactory = designerFactory;
        _runs = runs;
        _events = events;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(5);
        _gates = gates;
    }

    public TimeSpan Interval => _interval;

    public async Task RunAsync(CancellationToken ct)
    {
        // Stagger the first tick so we don't fight the dashboard at boot.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
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
        if (_gates is not null && await _gates.IsHeldAsync(Core.StageGates.Design, ct))
        {
            _logger.LogInformation("DesignerScheduler: held by operator gate; skipping tick");
            return;
        }

        IReadOnlyList<SpecRecord> candidates;
        try
        {
            candidates = await _specs.ListAsync(projectId: null, status: SpecStatus.ReadyForDesign, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DesignerScheduler: list specs failed; skipping tick");
            return;
        }

        foreach (var spec in candidates)
        {
            if (!await ShouldDesignAsync(spec, ct)) continue;
            try
            {
                await RunOneAsync(spec, DesignerTriggerKind.Scheduled, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DesignerScheduler: spec {Id} failed; continuing", spec.Id);
            }
        }
    }

    /// <summary>
    /// Heuristic: design a spec at most every <c>Interval</c>, or
    /// immediately if the last run failed. The recent-run lookup is
    /// <c>SELECT ts, status FROM designer_run WHERE spec_id = $id
    /// ORDER BY ts DESC LIMIT 1</c>.
    /// </summary>
    private async Task<bool> ShouldDesignAsync(SpecRecord spec, CancellationToken ct)
    {
        var recent = await _runs.ListAsync(specId: spec.Id, limit: 1, ct);
        if (recent.Count == 0) return true;
        var last = recent[0];
        if (last.Status is DesignerRunStatus.HygieneFailed or DesignerRunStatus.LlmFailed) return true;
        return DateTime.UtcNow - last.Ts >= _interval;
    }

    private async Task RunOneAsync(SpecRecord spec, DesignerTriggerKind trigger, CancellationToken ct)
    {
        var agent = _designerFactory.Create();
        var result = await agent.DesignSpecAsync(spec.Id, trigger, ct);
        if (!result.Success)
        {
            _logger.LogWarning("DesignerScheduler: spec {Id} failed: {Err}", spec.Id, result.Error);
        }
        else
        {
            _logger.LogInformation("DesignerScheduler: spec {Id} -> {Status} (artifacts={N})",
                spec.Id, result.NewSpecStatus, result.ArtifactIds.Count);
        }
    }
}

/// <summary>
/// Factory for DesignerAgent instances. Each scheduled or manual
/// design run builds a fresh agent. Mirrors <c>GroomerAgentFactory</c>.
/// </summary>
public sealed class DesignerAgentFactory
{
    private readonly ISpecStore _specs;
    private readonly DesignArtifactStore _artifacts;
    private readonly DesignerRunStore _runs;
    private readonly MemoryStore _memory;
    private readonly DesignHygieneChecker _hygiene;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly IDashboardEventBus _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _rolePromptsRoot;

    public DesignerAgentFactory(
        ISpecStore specs,
        DesignArtifactStore artifacts,
        DesignerRunStore runs,
        MemoryStore memory,
        DesignHygieneChecker hygiene,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ILoggerFactory loggerFactory,
        string rolePromptsRoot = "agents")
    {
        _specs = specs;
        _artifacts = artifacts;
        _runs = runs;
        _memory = memory;
        _hygiene = hygiene;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _loggerFactory = loggerFactory;
        _rolePromptsRoot = rolePromptsRoot;
    }

    public DesignerAgent Create() => new(
        _specs, _artifacts, _runs, _memory, _hygiene, _chatClientFactory, _config, _roles, _events,
        _loggerFactory.CreateLogger<DesignerAgent>(), _rolePromptsRoot);
}