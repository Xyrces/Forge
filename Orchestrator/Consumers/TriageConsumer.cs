using Forge.Agents;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Messaging;
using Forge.Projects;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>
/// Triage-agent runner (phase 2). Owns the forge.triage-requested topic
/// (competing-consumer transport: no other consumer may subscribe here).
/// No poller — the FailureTriageConsumer publishes the hint after opening
/// a ledger row when the project's triage flag is on and the task is
/// under the daily action cap.
///
/// Hints, not truth: every event re-reads the flag, the task, and the
/// ledger, and re-evaluates the deterministic guardrails
/// (<see cref="TriageGuardrails"/>) before the agent runs. Guardrail
/// trips park deterministically (no LLM). The agent itself can only act
/// through <see cref="TriageTools"/> — requeue-with-guidance, park,
/// flag-bug — each audited on the ledger with actor=triage.
/// </summary>
public sealed class TriageConsumer : EventConsumer<TriageRequested>
{
    private readonly ProjectContextFactory _projectContexts;
    private readonly Func<Forge.Projects.ProjectContext, ITriageRunner> _runnerFactory;
    private readonly ILogger<TriageConsumer> _logger;

    public TriageConsumer(
        ITransport transport,
        ProjectContextFactory projectContexts,
        Func<Forge.Projects.ProjectContext, ITriageRunner> runnerFactory,
        ILogger<TriageConsumer> logger)
        : base(transport, logger)
    {
        _projectContexts = projectContexts;
        _runnerFactory = runnerFactory;
        _logger = logger;
    }

    protected override async Task HandleAsync(TriageRequested evt, CancellationToken ct)
    {
        var ctx = _projectContexts.Find(evt.ProjectId);
        if (ctx is null)
        {
            _logger.LogWarning("Triage: unknown project {ProjectId} — hint dropped (task {TaskId})",
                evt.ProjectId, evt.TaskId);
            return;
        }
        // The flag is re-read, never trusted from the hint: a project
        // whose flag flipped off between publish and handling runs
        // nothing.
        if (!ctx.Options.TriageEnabled)
        {
            _logger.LogDebug("Triage: project {ProjectId} flag off — hint dropped (task {TaskId})",
                evt.ProjectId, evt.TaskId);
            return;
        }

        var task = await ctx.Issues.GetAsync(evt.TaskId, ct);
        if (task is null) return;
        var open = await ctx.Triage.GetOpenForTaskAsync(evt.TaskId, ct);
        if (open is not { Action: null })
        {
            // Already cleared/actioned between publish and handling.
            return;
        }

        var history = await ctx.Triage.ListForTaskAsync(evt.TaskId, ct);
        var decision = TriageGuardrails.Evaluate(history, open.Signature, DateTime.UtcNow);
        if (decision is not TriageGuardrails.Decision.Allowed)
        {
            var tools = new TriageTools(ctx.Issues, ctx.Triage, lifecycle: null,
                _logger);
            await tools.ParkForOperatorAsync(evt.TaskId, decision.ParkReason(), ct);
            return;
        }

        var runner = _runnerFactory(ctx);
        var result = await runner.RunAsync(evt.TaskId, open.Signature, open.Classification, ct);
        if (result.Error is not null)
        {
            _logger.LogWarning("Triage: run for {TaskId} errored ({Error}) — row stays open",
                evt.TaskId, result.Error);
        }
        else if (!result.ActionTaken)
        {
            _logger.LogWarning("Triage: run for {TaskId} took no action — row stays open for the operator",
                evt.TaskId);
        }
    }
}
