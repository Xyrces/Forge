using Forge.Agents;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Messaging;
using Forge.Projects;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Consumers;

/// <summary>
/// Failure-ledger writer. Owns the forge.task-failure-signal topic
/// (competing-consumer transport: no other consumer may subscribe here).
///
///   Failure          → open a ledger row (classify via the deterministic
///                      <see cref="FailureSignatureClassifier"/>); an
///                      already-actioned row closes 'failed-again' and a
///                      new row opens against the same signature. Then the
///                      phase-2 trigger: publish TriageRequested when the
///                      project opted in and the guardrails allow, else
///                      park deterministically (MaybeKickTriageAsync).
///   Clearance        → record the operator's action (requeue / close /
///                      reset-strikes, from the task's clearanceAction
///                      metadata the endpoints stamp) on the open row.
///   SuccessCandidate → close a cleared row's pending outcome as
///                      'succeeded'.
///
/// Hints, not truth: every branch re-reads the task + ledger rows and
/// the store guards (action IS NULL / outcome='pending') make
/// redelivery idempotent.
/// </summary>
public sealed class FailureTriageConsumer : EventConsumer<TaskFailureSignal>
{
    private readonly ProjectContextFactory _projectContexts;
    private readonly IEventPublisher _events;
    private readonly ILogger<FailureTriageConsumer> _logger;

    public FailureTriageConsumer(
        ITransport transport,
        ProjectContextFactory projectContexts,
        IEventPublisher events,
        ILogger<FailureTriageConsumer> logger)
        : base(transport, logger)
    {
        _projectContexts = projectContexts;
        _events = events;
        _logger = logger;
    }

    protected override async Task HandleAsync(TaskFailureSignal evt, CancellationToken ct)
    {
        var ctx = _projectContexts.Find(evt.ProjectId);
        if (ctx is null)
        {
            _logger.LogWarning("FailureTriage: unknown project {ProjectId} — hint dropped (task {TaskId})",
                evt.ProjectId, evt.TaskId);
            return;
        }
        var task = await ctx.Issues.GetAsync(evt.TaskId, ct);
        var triage = ctx.Triage;
        var open = await triage.GetOpenForTaskAsync(evt.TaskId, ct);

        switch (evt.Kind)
        {
            case FailureSignalKind.Failure:
            {
                if (task is null) return;
                var error = evt.ErrorExcerpt ?? task.GetMetadata("lastError");
                var (signature, classification) = FailureSignatureClassifier.Classify(
                    error, MetadataOf(task));
                var excerpt = error is { Length: > 300 } ? error[..300] : error;
                if (open is { Action: not null })
                {
                    // Cleared but the redispatch failed again: close the
                    // old row and re-open against the same signature.
                    await triage.CloseOutcomeAsync(open.Id, FailureTriageOutcomes.FailedAgain, ct);
                    await triage.OpenAsync(evt.TaskId, DateTime.UtcNow, signature, classification, excerpt, ct);
                }
                else if (open is not null)
                {
                    // Same open failure re-signalled (redelivery, or a
                    // Failed→Blocked re-failure before clearance).
                    await triage.UpdateOpenAsync(open.Id, DateTime.UtcNow, signature, classification, excerpt, ct);
                }
                else
                {
                    await triage.OpenAsync(evt.TaskId, DateTime.UtcNow, signature, classification, excerpt, ct);
                }
                await MaybeKickTriageAsync(ctx, evt.TaskId, signature, ct);
                break;
            }
            case FailureSignalKind.Clearance:
            {
                if (open is not { Action: null }) return;
                var closed = string.Equals(evt.ToStatus, nameof(IssueStatus.Closed), StringComparison.Ordinal);
                var action = task?.GetMetadata("clearanceAction")
                    ?? (closed ? FailureTriageActions.OperatorClose : FailureTriageActions.OperatorRequeue);
                await triage.RecordActionAsync(
                    open.Id, action, "operator", DateTime.UtcNow,
                    closed ? null : FailureTriageOutcomes.Pending, ct);
                break;
            }
            case FailureSignalKind.SuccessCandidate:
            {
                if (open is { Outcome: FailureTriageOutcomes.Pending })
                    await triage.CloseOutcomeAsync(open.Id, FailureTriageOutcomes.Succeeded, ct);
                break;
            }
        }
    }

    /// <summary>Phase-2 trigger (no poller): after opening/refreshing a
    /// ledger row, kick the triage agent when the project opted in and
    /// the guardrails allow it. At-cap / burn-loop failures park
    /// deterministically right here — no LLM, no event. Flag off → no
    /// TriageRequested, zero behavior change.</summary>
    private async Task MaybeKickTriageAsync(
        Forge.Projects.ProjectContext ctx, string taskId, string signature, CancellationToken ct)
    {
        if (!ctx.Options.TriageEnabled) return;
        var history = await ctx.Triage.ListForTaskAsync(taskId, ct);
        var decision = TriageGuardrails.Evaluate(history, signature, DateTime.UtcNow);
        if (decision is TriageGuardrails.Decision.Allowed)
        {
            var occurred = DateTimeOffset.UtcNow;
            await _events.PublishAsync(new TriageRequested
            {
                MessageId = TriageRequested.IdFor(ctx.Options.Id, taskId, occurred),
                ProjectId = ctx.Options.Id,
                TaskId = taskId,
                OccurredAt = occurred,
            }, ct);
            return;
        }
        _logger.LogInformation("FailureTriage: guardrail {Decision} for {TaskId} — deterministic park, no triage run",
            decision, taskId);
        var tools = new TriageTools(ctx.Issues, ctx.Triage, lifecycle: null, _logger);
        await tools.ParkForOperatorAsync(taskId, decision.ParkReason(), ct);
    }

    private static IReadOnlyDictionary<string, string>? MetadataOf(IssueRecord task)
    {
        if (string.IsNullOrWhiteSpace(task.MetadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(task.MetadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // JSON null is the delete idiom: a cleared key reads as absent.
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                dict[prop.Name] = prop.Value.ToString();
            }
            return dict;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
