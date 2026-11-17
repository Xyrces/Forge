using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Core.Initiatives;
using Forge.Dashboard;
using Forge.Specs;

namespace Forge.Agents;

/// <summary>Durable approval/ingestion/refinement reconciliation; events only reduce wake-up latency.</summary>
public sealed class ProductRefinementQueue : IAsyncDisposable
{
    private readonly InitiativeApprovalService _approvals;
    private readonly Func<IReadOnlyList<string>> _projectIds;
    private readonly Func<string, InitiativeIngestionStore> _ingestion;
    private readonly Func<string, ISpecStore> _specs;
    private readonly Func<string, string, CancellationToken, Task<SpecRecord?>> _refine;
    private readonly ILogger<ProductRefinementQueue> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _worker;
    private readonly Task _events;
    private readonly HashSet<string> _recovered = new(StringComparer.OrdinalIgnoreCase);

    public ProductRefinementQueue(InitiativeApprovalService approvals, Func<IReadOnlyList<string>> projectIds,
        Func<string, InitiativeIngestionStore> ingestion, Func<string, ISpecStore> specs,
        Func<string, string, CancellationToken, Task<SpecRecord?>> refine,
        IDashboardEventBus bus, ILogger<ProductRefinementQueue> logger)
    {
        _approvals=approvals; _projectIds=projectIds; _ingestion=ingestion; _specs=specs; _refine=refine; _logger=logger;
        _events = WatchAsync(bus.Subscribe(), _cts.Token);
        _worker = RunAsync(_cts.Token);
    }

    public int PendingCount => 0;
    private async Task WatchAsync(ChannelReader<DashboardEvent> reader, CancellationToken ct)
    {
        try { await foreach(var ev in reader.ReadAllAsync(ct))
            if(ev.Kind == "intake.epic.accepted" || ev.Kind == "intake.refinement.retry") _wake.Writer.TryWrite(true); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                await ReconcileAsync(ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hint = _wake.Reader.ReadAsync(wait.Token).AsTask();
                var timer = Task.Delay(TimeSpan.FromSeconds(30),wait.Token);
                await Task.WhenAny(hint,timer);
                await wait.CancelAsync();
                try { await Task.WhenAll(hint,timer); }
                catch(OperationCanceledException) when(wait.IsCancellationRequested) { }
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        foreach(var project in _projectIds())
        {
            try
            {
                var target = _ingestion(project);
                // Exclusive controller startup only. An interrupted paid call
                // is uncertain, not permission to spend again automatically.
                if(!_recovered.Contains(project)) { await target.RecoverRunningAsync(ct); _recovered.Add(project); }
                string? approvalCursor = null;
                while(true)
                {
                    var approvedPage = await _approvals.Proposals.ListApprovedAsync(project,ct,afterId:approvalCursor);
                    foreach(var summary in approvedPage)
                    {
                        // Acknowledged ingestion does not need to be recreated or
                        // republished on every reconciliation tick.
                        if(summary.EpicId is null)
                        {
                            var proposal = await _approvals.Proposals.GetAsync(project,summary.Id,ct)
                                ?? throw new InvalidOperationException("Approved proposal disappeared.");
                            await _approvals.IngestApprovedAsync(proposal,ct);
                        }
                    }
                    if(approvedPage.Count < 50) break;
                    approvalCursor=approvedPage[^1].Id;
                }
                string? refinementCursor = null;
                while(true)
                {
                var pending = await target.ListPendingRefinementAsync(ct,afterId:refinementCursor);
                foreach(var receipt in pending)
                {
                    if(!await target.TryBeginRefinementAsync(receipt.ProposalId,receipt.Revision,ct)) continue;
                    try
                    {
                        var spec = await _specs(project).GetAsync(receipt.SpecId,ct)
                            ?? throw new InvalidOperationException("Approved initiative spec is missing.");
                        if(spec.Status == SpecStatus.Draft)
                        {
                            var result = await _refine(project,receipt.SpecId,ct);
                            if(result is null) throw new InvalidOperationException("Product refinement did not commit a spec update; explicit retry is required.");
                        }
                        if(!await target.CompleteRefinementAsync(receipt.ProposalId,receipt.Revision,ct))
                            throw new InvalidOperationException("Refinement completion could not be committed.");
                    }
                    catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                    catch(Exception ex)
                    {
                        _logger.LogError(ex,"Initiative refinement failed for {Proposal}",receipt.ProposalId);
                        await target.RecordRefinementFailureAsync(receipt.ProposalId,receipt.Revision,
                            "Refinement failed; inspect the run and explicitly retry. " + ex.GetType().Name,ct);
                    }
                }
                if(pending.Count < 50) break;
                refinementCursor=pending[^1].ProposalId;
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex) { _logger.LogError(ex,"Initiative reconciliation failed for {Project}",project); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Task.WhenAll(_worker,_events);
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
    private readonly string _rolePromptsRoot;

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
        string rolePromptsRoot = "agents")
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
        _rolePromptsRoot = rolePromptsRoot;
    }

    public ProductAgent Create() => new(
        _specs, _issues, _projectContext, _chatClientFactory, _config, _roles,
        _events, _loggerFactory.CreateLogger<ProductAgent>(), _skills, _rolePromptsRoot);
}
