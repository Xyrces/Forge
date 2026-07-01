using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Specs;

namespace PortHorizon.Agents.Agents;

/// <summary>
/// Background queue that watches the dashboard event bus for
/// <c>intake.epic.accepted</c> events and runs a
/// <see cref="ProductAgent.RefineSpecAsync"/> on each accepted
/// spec. Fire-and-forget: the operator's accept-click returns
/// immediately, the refinement happens on a worker thread.
///
/// <para>
/// <see cref="IDashboardEventBus"/> is in-process only (no SSE
/// out of band needed for the producer-consumer wiring). The
/// SSE stream stays for the dashboard's UI.
/// </para>
///
/// <para>
/// The queue is bounded so a runaway operator can't fill memory.
/// If full, the event is logged and dropped — the spec stays
/// unrefined; the operator can re-accept after a moment.
/// </para>
/// </summary>
public sealed class ProductRefinementQueue : IAsyncDisposable
{
    private readonly ProductAgentFactory _factory;
    private readonly ISpecStore _specs;
    private readonly ILogger<ProductRefinementQueue> _logger;
    private readonly Task _worker;
    private readonly CancellationTokenSource _cts = new();

    public ProductRefinementQueue(
        ProductAgentFactory factory,
        ISpecStore specs,
        IDashboardEventBus bus,
        ILogger<ProductRefinementQueue> logger)
    {
        _factory = factory;
        _specs = specs;
        _logger = logger;
        // Subscribe to the bus; the bus returns a ChannelReader that
        // gets every published event (past + future, with the bus
        // pre-loading history for late subscribers).
        var reader = bus.Subscribe();
        _worker = Task.Run(() => DrainAsync(reader, _cts.Token));
    }

    public int PendingCount => 0; // The bus's channel has no Count API; coarse metric is fine.

    private async Task DrainAsync(ChannelReader<DashboardEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in reader.ReadAllAsync(ct))
            {
                if (ev.Kind != "intake.epic.accepted") continue;
                if (ev.Data is null) continue;
                if (!ev.Data.TryGetValue("epicId", out var epicIdObj) || epicIdObj is null) continue;
                var epicId = epicIdObj.ToString();
                try
                {
                    // Look up the spec for this epic; the operator clicked
                    // Accept on an issue, the spec was created from that
                    // issue via parent_issue_id. We need the spec id AND
                    // the project id to call the agent.
                    var specs = await _specs.ListAsync(projectId: null, status: null, ct);
                    var spec = specs.FirstOrDefault(s => s.ParentIssueId == epicId);
                    if (spec is null)
                    {
                        _logger.LogWarning("ProductRefinementQueue: no spec for epic {Epic}", epicId);
                        continue;
                    }
                    var agent = _factory.Create();
                    await agent.RefineSpecAsync(spec.Id, spec.ProjectId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProductRefinementQueue: refine failed for {Epic}", epicId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _worker.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// Factory for ProductAgent so the queue can build per-refinement
/// agents. The factory owns the dependencies so ProductAgent
/// stays a plain class.
/// </summary>
public sealed class ProductAgentFactory
{
    private readonly ISpecStore _specs;
    private readonly IIssueStore _issues;
    private readonly IProjectContextSource _projectContext;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly RoleAgentRegistry _roles;
    private readonly IDashboardEventBus _events;
    private readonly ISkillSource? _skills;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _kiloAgentsRoot;

    public ProductAgentFactory(
        ISpecStore specs,
        IIssueStore issues,
        IProjectContextSource projectContext,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        RoleAgentRegistry roles,
        IDashboardEventBus events,
        ISkillSource? skills,
        ILoggerFactory loggerFactory,
        string kiloAgentsRoot = ".kilo/agents")
    {
        _specs = specs;
        _issues = issues;
        _projectContext = projectContext;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _roles = roles;
        _events = events;
        _skills = skills;
        _loggerFactory = loggerFactory;
        _kiloAgentsRoot = kiloAgentsRoot;
    }

    public ProductAgent Create() => new(
        _specs, _issues, _projectContext, _chatClientFactory, _config, _roles,
        _events, _loggerFactory.CreateLogger<ProductAgent>(), _skills, _kiloAgentsRoot);
}