using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Orchestrator.Workflow;

namespace Forge.Orchestrator;

/// <summary>
/// P4 Stage B — abstraction over where the engineering dispatch
/// workflow runs.
///
/// <para>
/// Two implementations:
/// <list type="bullet">
///   <item><see cref="InProcessDispatcher"/> — runs the
///   <see cref="EngineeringDispatchWorkflow"/> via
///   <c>InProcessExecution.Default</c>. State lives in-process;
///   restart safety is provided by P4 Stage A's
///   <see cref="StartupRecovery"/>.</item>
///   <item><see cref="DurableDispatcher"/> — registers the
///   same workflow with
///   <c>Microsoft.Agents.AI.DurableTask.ConfigureDurableWorkflows</c>.
///   The workflow runs in a Durable Task Scheduler sidecar
///   (DTS emulator for dev; Azure DTS for prod). The DTS
///   persists workflow state across orchestrator crashes; the
///   in-process recovery service becomes redundant for
///   engineering-dispatch work (still useful for the
///   Designer / Artist / Groomer schedulers which use fresh
///   MAF agents per run).</item>
/// </list>
/// </para>
///
/// <para>
/// The switch is <see cref="OrchestratorOptions.Execution"/> in
/// <c>appsettings.json</c>: <c>"InProcess"</c> (default) or
/// <c>"Durable"</c>. The InProcess runtime is unchanged; the
/// Durable runtime requires <see cref="OrchestratorOptions.DtsConnectionString"/>
/// + a reachable DTS sidecar at localhost:8080 (or the URL the
/// connection string encodes).
/// </para>
/// </summary>
public interface IWorkflowDispatcher
{
    /// <summary>
    /// Dispatch an issue and BLOCK until the workflow's
    /// workflow-run reaches a terminal state. Mirrors the
    /// pre-Stage-B <see cref="Workflow.EngineeringDispatchWorkflow.RunAsync"/>
    /// contract so callers can keep their synchronous
    /// dispatch-then-check-shape.
    /// </summary>
    /// <remarks>
    /// For <see cref="InProcessDispatcher"/> the workflow runs
    /// on the calling thread; for
    /// <see cref="DurableDispatcher"/> it runs on the DTS
    /// worker thread and the dispatcher waits on the durable
    /// run handle.
    /// </remarks>
    Task DispatchAsync(IssueRecord issue, CancellationToken ct);

    /// <summary>
    /// Wait for the orchestrator host to be ready. For
    /// <see cref="InProcessDispatcher"/> this is a no-op.
    /// For <see cref="DurableDispatcher"/> this blocks until
    /// the DTS sidecar is reachable and the worker has started.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct);
}

/// <summary>
/// InProcess runtime — same code path as before Stage B. The
/// dispatch fires off a Task that calls
/// <c>Workflow.RunAsync</c> via
/// <c>InProcessExecution.Default</c>. State is in-process;
/// crash recovery is the StartupRecovery service's job.
/// </summary>
public sealed class InProcessDispatcher : IWorkflowDispatcher
{
    private readonly Func<IssueRecord, CancellationToken, Task> _runOne;
    private readonly ILogger<InProcessDispatcher> _logger;

    public InProcessDispatcher(
        Func<IssueRecord, CancellationToken, Task> runOne,
        ILogger<InProcessDispatcher> logger)
    {
        _runOne = runOne;
        _logger = logger;
    }

    public Task DispatchAsync(IssueRecord issue, CancellationToken ct)
        => _runOne(issue, ct);

    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Base for the Durable dispatcher. The runtime requires a
/// running DTS sidecar; <see cref="EnsureReadyAsync"/> waits for
/// the gRPC port to be reachable before the dispatch loop
/// starts. <see cref="Dispatch"/> writes the workflow to the
/// DTS sidecar via <c>IWorkflowClient.RunAsync</c> (an external
/// event / activity — the actual execution happens on the
/// worker thread in the DTS).
/// </summary>
public abstract class DurableDispatcherBase : IWorkflowDispatcher
{
    /// <summary>Inject the workflow client. Tests can supply an
    /// in-process emulator client; production wires in the
    /// Durable Task Scheduler sidecar client.</summary>
    public abstract Task DispatchAsync(IssueRecord issue, CancellationToken ct);
    public abstract Task EnsureReadyAsync(CancellationToken ct);
}