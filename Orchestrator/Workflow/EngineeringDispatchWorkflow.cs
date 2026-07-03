using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;

namespace PortHorizon.Agents.Orchestrator.Workflow;

/// <summary>
/// Builds and runs the engineering dispatch workflow. The
/// workflow shape is:
///
///   Claim -> Worktree -> RunAgent -> CommitPushPr -> EnqueueWatch
///
/// Each stage is a <see cref="Microsoft.Agents.AI.Workflows.Executor"/>
/// with typed input/output. AlreadyClaimed / NoDiff / Skipped are
/// first-class result variants the workflow edge short-circuits on;
/// they're declared via the typed TIn/TOut generics so the
/// runtime can route them without conditional edges.
/// </summary>
public sealed class EngineeringDispatchWorkflow
{
    private readonly ClaimExecutor _claim;
    private readonly WorktreeExecutor _worktree;
    private readonly RunAgentExecutor _runAgent;
    private readonly CommitPushPrExecutor _commitPushPr;
    private readonly EnqueueWatchExecutor _enqueueWatch;
    private readonly ILogger<EngineeringDispatchWorkflow> _logger;

    public EngineeringDispatchWorkflow(
        IIssueStore issues,
        IAgentRunner agentRunner,
        GitWorktreeService worktrees,
        GitHubService gitHub,
        RoleAgentRegistry roleRegistry,
        WorkspaceOptions workspaceOptions,
        IDashboardEventBus events,
        Func<string, string?> drainMessageBus,
        DesignArtifactStore designArtifacts,
        ILogger<EngineeringDispatchWorkflow> logger)
    {
        var nullFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _claim = new ClaimExecutor(issues, nullFactory.CreateLogger<ClaimExecutor>());
        _worktree = new WorktreeExecutor(issues, worktrees, workspaceOptions.DefaultBranch,
            nullFactory.CreateLogger<WorktreeExecutor>());
        _runAgent = new RunAgentExecutor(issues, agentRunner, roleRegistry,
            drainMessageBus, events, designArtifacts,
            nullFactory.CreateLogger<RunAgentExecutor>());
        _commitPushPr = new CommitPushPrExecutor(issues, worktrees, gitHub, events,
            nullFactory.CreateLogger<CommitPushPrExecutor>());
        _enqueueWatch = new EnqueueWatchExecutor(issues,
            nullFactory.CreateLogger<EnqueueWatchExecutor>());
        _logger = logger;
    }

    /// <summary>
    /// Build the workflow graph. Returns a <see cref="Workflow"/>
    /// the caller can run via <c>InProcessExecution.RunAsync</c> or
    /// any other MAF runtime.
    /// </summary>
    public Microsoft.Agents.AI.Workflows.Workflow Build()
    {
        var wb = new WorkflowBuilder(_claim);
        wb.AddEdge(_claim, _worktree);
        wb.AddEdge(_worktree, _runAgent);
        wb.AddEdge(_runAgent, _commitPushPr);
        wb.AddEdge(_commitPushPr, _enqueueWatch);
        return wb.Build();
    }

    /// <summary>
    /// Run the workflow on a single issue, blocking until
    /// completion. Each executor publishes its work via
    /// <see cref="IDashboardEventBus"/> + IIssueStore state
    /// transitions; we don't surface a return value because the
    /// MAF Run API doesn't surface the workflow's final executor
    /// output cleanly. Callers watch the dashboard event stream
    /// for the result, same as before this refactor.
    /// </summary>
    public async Task RunAsync(IssueRecord issue, CancellationToken ct)
    {
        var workflow = Build();
        var env = InProcessExecution.Default;
        await using var run = await env.RunAsync<IssueRecord>(workflow, issue, cancellationToken: ct);
        // The run completes when the workflow halts. Per-executor
        // outputs are published through the dashboard event bus +
        // persisted in the issue store (see each executor's
        // HandleAsync).
    }
}