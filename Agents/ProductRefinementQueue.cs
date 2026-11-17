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
    private readonly Func<string, string, string, CancellationToken, Task<SpecRecord?>>? _repair;
    private readonly ILogger<ProductRefinementQueue> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Task _worker = Task.CompletedTask;
    private Task _events = Task.CompletedTask;
    private readonly IDashboardEventBus _bus;
    private CancellationTokenRegistration _shutdownRegistration;
    private int _started;
    private readonly HashSet<string> _recovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Project, string Proposal, int Revision), string> _failureAcks = new();

    public ProductRefinementQueue(InitiativeApprovalService approvals, Func<IReadOnlyList<string>> projectIds,
        Func<string, InitiativeIngestionStore> ingestion, Func<string, ISpecStore> specs,
        Func<string, string, CancellationToken, Task<SpecRecord?>> refine,
        IDashboardEventBus bus, ILogger<ProductRefinementQueue> logger,
        Func<string, string, string, CancellationToken, Task<SpecRecord?>>? repair = null)
    {
        _approvals = approvals;
        _projectIds = projectIds;
        _ingestion = ingestion;
        _specs = specs;
        _refine = refine;
        _repair = repair;
        _logger = logger;
        _bus = bus;
    }

    /// <summary>Called only after controller admission opens. Shutdown joins all refinement work.</summary>
    public Task RunAsync(CancellationToken shutdown)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Initiative refinement reconciliation already started.");
        _shutdownRegistration = shutdown.Register(() => _cts.Cancel());
        _events = WatchAsync(_bus.Subscribe(), _cts.Token);
        _worker = ReconcileLoopAsync(_cts.Token);
        return Task.WhenAll(_worker, _events);
    }

    private async Task WatchAsync(ChannelReader<DashboardEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in reader.ReadAllAsync(ct))
                if (ev.Kind == "intake.epic.accepted" || ev.Kind == "intake.refinement.retry")
                    _wake.Writer.TryWrite(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogError(ex, "Initiative project discovery failed; retrying on the next reconciliation"); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hint = _wake.Reader.ReadAsync(wait.Token).AsTask();
                var timer = Task.Delay(TimeSpan.FromSeconds(30), wait.Token);
                await Task.WhenAny(hint, timer);
                await wait.CancelAsync();
                try
                {
                    await Task.WhenAll(hint, timer);
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        foreach (var project in _projectIds())
        {
            try
            {
                var target = _ingestion(project);
                // Exclusive controller startup only. An interrupted paid call
                // is uncertain, not permission to spend again automatically.
                if (!_recovered.Contains(project))
                {
                    await target.RecoverRunningAsync(ct);
                    _recovered.Add(project);
                }
                // A failed write must not leave locally finished work appearing
                // Running forever. Retry only the acknowledgement, never generation.
                foreach (var ack in _failureAcks.Where(a => a.Key.Project == project).ToArray())
                {
                    await target.RecordRefinementFailureAsync(ack.Key.Proposal, ack.Key.Revision, ack.Value, ct);
                    _failureAcks.Remove(ack.Key);
                }
                string? approvalCursor = null;
                while (true)
                {
                    var approvedPage = await _approvals.Proposals.ListApprovedAsync(project, ct, afterId: approvalCursor);
                    foreach (var summary in approvedPage)
                    {
                        // Acknowledged ingestion does not need to be recreated or
                        // republished on every reconciliation tick.
                        if (summary.EpicId is null)
                        {
                            try
                            {
                                var proposal = await _approvals.Proposals.GetAsync(project, summary.Id, ct)
                                    ?? throw new InvalidOperationException("Approved proposal disappeared.");
                                await _approvals.IngestApprovedAsync(proposal, ct);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex) { _logger.LogError(ex, "Initiative ingestion pending recovery for {Proposal}", summary.Id); }
                        }
                    }
                    if (approvedPage.Count < 50)
                        break;
                    approvalCursor = approvedPage[^1].Id;
                }
                string? refinementCursor = null;
                while (true)
                {
                    var pending = await target.ListPendingRefinementAsync(ct, afterId: refinementCursor);
                    foreach (var receipt in pending)
                    {
                        if (!await target.TryBeginRefinementAsync(receipt.ProposalId, receipt.Revision, ct))
                            continue;
                        try
                        {
                            var spec = await _specs(project).GetAsync(receipt.SpecId, ct)
                                ?? throw new InvalidOperationException("Approved initiative spec is missing.");
                            if (spec.Status == SpecStatus.Draft)
                            {
                                var repairContext = await target.GetRepairContextAsync(receipt.SpecId, spec.CurrentVersion, ct);
                                if (repairContext is not null && _repair is null)
                                    throw new InvalidOperationException("Hygiene repair requires a diagnostic-aware Product callback.");
                                var result = repairContext is null
                                    ? await _refine(project, receipt.SpecId, ct)
                                    : await _repair!(project, receipt.SpecId, repairContext, ct);
                                if (result is null)
                                    throw new InvalidOperationException("Product refinement did not commit a spec update; explicit retry is required.");
                            }
                            if (!await target.CompleteRefinementAsync(receipt.ProposalId, receipt.Revision, ct))
                                throw new InvalidOperationException("Refinement completion could not be committed.");
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Initiative refinement failed for {Proposal}", receipt.ProposalId);
                            var key = (project, receipt.ProposalId, receipt.Revision);
                            var error = "Refinement failed; inspect the run and explicitly retry. " + ex.GetType().Name;
                            _failureAcks[key] = error;
                            await target.RecordRefinementFailureAsync(receipt.ProposalId, receipt.Revision,
                                error, ct);
                            _failureAcks.Remove(key);
                        }
                    }
                    if (pending.Count < 50)
                        break;
                    refinementCursor = pending[^1].ProposalId;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Initiative reconciliation failed for {Project}", project); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await Task.WhenAll(_worker, _events);
        _shutdownRegistration.Dispose();
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
