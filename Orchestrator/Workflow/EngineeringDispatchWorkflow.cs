using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator.Workflow;

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
        ArtOutputStore artOutputs,
        IMemoryExtractor memoryExtractor,
        MemoryExtractionStore extractionStore,
        ILogger<EngineeringDispatchWorkflow> logger,
        string? projectId = null,
        ILoggerFactory? loggerFactory = null,
        ISprintStore? sprints = null,
        double timeoutMinutes = 15.0,
        Core.TaskStateMachine? lifecycle = null,
        Core.Workflow.WorkflowResolver? workflow = null,
        IReadOnlyList<string>? verifyCommands = null)
    {
        // Executor loggers: production passes the real factory so
        // executor diagnostics (checkpoint advances, push/PR steps)
        // reach the journal; tests omit it and keep NullLogger.
        var nullFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _claim = new ClaimExecutor(issues, nullFactory.CreateLogger<ClaimExecutor>());
        _worktree = new WorktreeExecutor(issues, worktrees, workspaceOptions.DefaultBranch,
            nullFactory.CreateLogger<WorktreeExecutor>());
        _runAgent = new RunAgentExecutor(issues, agentRunner, roleRegistry,
            drainMessageBus, events, designArtifacts, artOutputs,
            nullFactory.CreateLogger<RunAgentExecutor>(), projectId, sprints, timeoutMinutes, lifecycle);
        _commitPushPr = new CommitPushPrExecutor(issues, worktrees, gitHub, events,
            memoryExtractor, extractionStore,
            nullFactory.CreateLogger<CommitPushPrExecutor>(), workflow, verifyCommands);
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
        // WithName is required for the Durable runtime's
        // DurableWorkflowOptions.AddWorkflow validation; the
        // InProcess runtime ignores the name. Defaulting to
        // "engineering-dispatch" keeps both runtimes happy.
        return wb.WithName("engineering-dispatch").Build();
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