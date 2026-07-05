using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Orchestrator;

/// <summary>
/// P4 Stage B — DurableDispatcher. Registers the engineering
/// dispatch workflow with
/// <c>Microsoft.Agents.AI.DurableTask.ConfigureDurableWorkflows</c>
/// so workflow state persists across orchestrator crashes.
///
/// <para>
/// The workflow definition itself is unchanged — it's the
/// same <see cref="Forge.Orchestrator.Workflow.EngineeringDispatchWorkflow"/>
/// the InProcess runtime uses. The DurableWorkflowOptions.AddWorkflow
/// API takes a fully-built <see cref="Workflow"/>; since our
/// executors take singletons from the DI container (no per-instance
/// scope), we build the workflow once at composition time and
/// reuse it for every orchestration instance.
/// </para>
///
/// <para>
/// <b>Actors:</b>
/// <list type="bullet">
///   <item><b>Worker</b> — runs the orchestrator + client in
///   one process. <see cref="BuildHost"/> builds it.</item>
///   <item><b>Client</b> — schedules new workflows + raises
///   external events. Co-located with the worker in our
///   setup.</item>
///   <item><b>DTS</b> — the sidecar that owns workflow state
///   + history. <c>podman-compose / docker compose -f
///   deploy/docker-compose.yml up -d</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// When the orchestrator crashes mid-workflow, the DTS keeps the
/// workflow's persistent state. On restart, the worker thread
/// resumes from the last durable checkpoint. P4 Stage A's
/// StartupRecovery becomes redundant for engineering-dispatch
/// work; it still helps the Designer / Artist / Groomer
/// schedulers (which use fresh MAF agents per run, not durable
/// workflows).
/// </para>
/// </summary>
public sealed class DurableDispatcher : DurableDispatcherBase
{
    private readonly OrchestratorOptions _options;
    private readonly Microsoft.Agents.AI.Workflows.Workflow _workflow;
    private readonly ILogger<DurableDispatcher> _logger;
    private readonly Func<IHost> _buildHost;
    private IHost? _host;

    public DurableDispatcher(
        OrchestratorOptions options,
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        ILogger<DurableDispatcher> logger,
        Func<IHost> buildHost)
    {
        _options = options;
        _workflow = workflow;
        _logger = logger;
        _buildHost = buildHost;
    }

    public override async Task EnsureReadyAsync(CancellationToken ct)
    {
        // Build + start the worker host. ConfigureDurableWorkflows
        // registers the workflow with the worker; the worker
        // registers with the DTS sidecar. Once StartAsync returns,
        // the worker is listening and ready to receive
        // orchestration requests from the client.
        if (_host is not null) return;
        _host = _buildHost();
        await _host.StartAsync(ct);
        _logger.LogInformation(
            "DurableDispatcher: worker host started. Execution=Durable, DTS={Cxn}",
            _options.DtsConnectionString);
    }

    public override async Task DispatchAsync(IssueRecord issue, CancellationToken ct)
    {
        // Schedule the workflow on the Durable client. The
        // client writes the orchestration instance to the DTS
        // sidecar; the worker thread picks it up and runs it.
        // We block on the durable run handle so the caller
        // (OrchestratorAgent) keeps its synchronous shape.
        if (_host is null)
        {
            _logger.LogError("DurableDispatcher: dispatch called before EnsureReadyAsync");
            return;
        }
        var client = _host.Services.GetRequiredService<IWorkflowClient>();
        var run = await client.RunAsync(_workflow, issue.Id, cancellationToken: ct);
        // The interface returns IWorkflowRun; the concrete
        // implementation also implements IAwaitableWorkflowRun
        // (per the MAF DurableTask docs). Pattern-match on that
        // interface to call WaitForCompletionAsync<TResult>.
        if (run is IAwaitableWorkflowRun awaitableRun)
        {
            // WaitForCompletionAsync<TResult> requires knowing
            // the workflow's output type. EngineeringDispatchWorkflow
            // doesn't surface one (each executor writes to the
            // DB / dashboard event bus). Use object? and discard.
            _ = await awaitableRun.WaitForCompletionAsync<object?>(ct);
        }
        else
        {
            // Fallback: poll NewEvents for a terminal WorkflowOutputEvent.
            while (!ct.IsCancellationRequested)
            {
                if (run.NewEvents.OfType<Microsoft.Agents.AI.Workflows.WorkflowOutputEvent>().Any())
                    break;
                if (run.NewEvents.OfType<Microsoft.Agents.AI.Workflows.WorkflowErrorEvent>().Any())
                    break;
                await Task.Delay(500, ct);
            }
        }
    }

    /// <summary>
    /// Build the host with the workflow registered. Called once
    /// from <see cref="EnsureReadyAsync"/>. The host runs the
    /// Durable Task worker in-process; the actual workflow
    /// execution is durable because state lives in the DTS.
    /// </summary>
    public static IHost BuildHost(
        IServiceProvider services,
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        OrchestratorOptions options)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(s =>
            {
                // Register the workflow as a singleton; it's
                // built once and shared across orchestrations
                // because its executors are stateless (they
                // read DI singletons at construction).
                s.AddSingleton(workflow);
                // Configure the Durable runtime. The
                // AddWorkflow call registers with the worker;
                // UseDurableTaskScheduler wires the worker +
                // client to the DTS sidecar via gRPC.
                s.ConfigureDurableWorkflows(
                    workflowOptions => workflowOptions.AddWorkflow(workflow),
                    workerBuilder: builder => builder.UseDurableTaskScheduler(options.DtsConnectionString),
                    clientBuilder: builder => builder.UseDurableTaskScheduler(options.DtsConnectionString));
            })
            .Build();
    }
}