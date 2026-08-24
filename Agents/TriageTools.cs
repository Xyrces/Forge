using Forge.Core;
using Microsoft.Extensions.Logging;

namespace Forge.Agents;

/// <summary>
/// The triage agent's bounded action space (phase 2, plan §3). Every
/// action writes the failure ledger with actor=triage and stamps the
/// task's triageAction/triageNote metadata (rendered on TaskDetail).
/// Store guards make every action idempotent: the open row's
/// action IS NULL check means a double-fire records nothing twice.
///
/// Also used directly by the deterministic guardrails (daily cap /
/// burn-loop park) — a guardrail park is a triage-system action and
/// audits identically.
/// </summary>
public sealed class TriageTools
{
    private readonly IIssueStore _issues;
    private readonly FailureTriageStore _triage;
    private readonly TaskStateMachine? _lifecycle;
    private readonly TriageEscalationContext? _escalation;
    private readonly ILogger _logger;

    public TriageTools(
        IIssueStore issues,
        FailureTriageStore triage,
        TaskStateMachine? lifecycle,
        ILogger logger,
        TriageEscalationContext? escalation = null)
    {
        _issues = issues;
        _triage = triage;
        _lifecycle = lifecycle;
        _logger = logger;
        _escalation = escalation;
    }

    /// <summary>The shared guard preamble for the requeue-style
    /// actions (requeue_with_guidance, escalate_model): task exists,
    /// is Failed or Blocked, has an open ledger row, and the row is
    /// not already actioned. One helper so a future guard lands on
    /// BOTH actions at once — hand-maintained copies drift (park/flag
    /// deliberately use the three-guard variant without the status
    /// check).</summary>
    private async Task<(IssueRecord Task, FailureTriageEntry Open, string? Error)> GuardRequeueStyleAsync(
        string taskId, string verb, CancellationToken ct)
    {
        var task = await _issues.GetAsync(taskId, ct);
        if (task is null) return (null!, null!, $"error: task {taskId} not found");
        if (task.Status is not (IssueStatus.Failed or IssueStatus.Blocked))
            return (null!, null!, $"error: task {taskId} is {task.Status} — only Failed or Blocked tasks can be {verb}");
        var open = await _triage.GetOpenForTaskAsync(taskId, ct);
        if (open is null) return (null!, null!, $"error: task {taskId} has no open ledger row — nothing to action");
        if (open.Action is not null) return (null!, null!, $"error: task {taskId}'s ledger row is already actioned ({open.Action})");
        return (task, open, null);
    }

    /// <summary>Requeue the failed/blocked task with an evidence-cited
    /// reorientation that rides the next run's prompt (reworkReason /
    /// reworkContext). Unlike an operator requeue the strike budget is
    /// NOT reset — triage requeues consume rounds deliberately (plan
    /// §4); the 3-strike breaker still applies.</summary>
    public async Task<string> RequeueWithGuidanceAsync(
        string taskId, string signature, string note, string? context, CancellationToken ct = default)
    {
        var (task, open, guardError) = await GuardRequeueStyleAsync(taskId, "requeued", ct);
        if (guardError is not null) return guardError;

        // Ledger first: the boundary crossing below publishes a
        // Clearance signal, and the FailureTriageConsumer's branch
        // no-ops on an already-actioned row — recording here first is
        // what keeps the actor 'triage' instead of 'operator'.
        await _triage.RecordActionAsync(
            open.Id, FailureTriageActions.TriageRequeue, FailureTriageActors.Triage,
            DateTime.UtcNow, FailureTriageOutcomes.Pending, ct);

        var guidance = context is null ? note : $"{note}\n\n{context}";
        var meta = new Dictionary<string, object>
        {
            // Stale-error display clears; strike counters
            // (retryCount/reworkAttempts/noProgressAttempts/review*)
            // deliberately survive — a triage requeue SPENDS a round.
            ["lastError"] = null!,
            ["lastErrorAt"] = null!,
            // Fossil-head guard: same deadlock class as the operator
            // requeue — a stale reworkForSha can swallow the next
            // legitimate rework verdict.
            ["reworkForSha"] = null!,
            ["reworkReason"] = $"triage: {signature}",
            ["reworkContext"] = guidance,
            ["requeuedFromFailedAt"] = DateTime.UtcNow.ToString("O"),
            ["triageAction"] = "requeue",
            ["triageNote"] = note,
            ["triageActionAt"] = DateTime.UtcNow.ToString("O"),
        };
        if (task.GetMetadata("prNumber") is not null)
            meta["prOpenedAt"] = DateTime.UtcNow.ToString("O");
        await _issues.TransitionAsync(taskId, IssueStatus.Pending,
            $"triage requeue with guidance ({signature})", meta, ct);

        // Same lifecycle repair as the operator requeue endpoint:
        // without the machine report the state stays Failed and the
        // next dispatch violates.
        if (_lifecycle is not null)
        {
            var requeued = await _issues.GetAsync(taskId, ct);
            if (requeued is not null)
                await _lifecycle.ReportAsync(_issues, requeued, TaskEvent.OperatorRequeue,
                    watch: null, hasActiveDevRun: false, ct);
        }
        _logger.LogInformation("Triage: requeued {TaskId} with guidance (signature {Signature})", taskId, signature);
        return $"ok: requeued {taskId} with guidance; the failure ledger records actor=triage";
    }

    /// <summary>Phase 3: escalate the task's next dev run to the
    /// role's configured ESCALATION model. Writes the single-shot
    /// llm/taskModel marker and requeues the task (the escalated run
    /// resolves the ledger outcome, same as a requeue). The agent
    /// never picks a model — the tool validates that the task's role
    /// HAS one configured (explicit-only escalation, operator
    /// decision 2026-08-23); unset → error, nothing written, no
    /// action spent.</summary>
    public async Task<string> EscalateModelAsync(
        string taskId, string signature, string note, CancellationToken ct = default)
    {
        var (task, open, guardError) = await GuardRequeueStyleAsync(taskId, "escalated", ct);
        if (guardError is not null) return guardError;
        if (_escalation is null)
            return "error: model escalation is not wired in this deployment";
        var role = RoleAgentRegistry.FromTaskType(task.Type);
        var target = _escalation.Config.ResolveEscalationEffective(
            role, _escalation.Overrides, _escalation.ProjectId);
        if (target is null)
        {
            var roleName = role.ToString().ToLowerInvariant();
            return $"error: no escalation model configured for {roleName} — set one on /agents";
        }

        // Ledger first: the boundary crossing below publishes a
        // Clearance signal, and the FailureTriageConsumer's branch
        // no-ops on an already-actioned row — recording here first is
        // what keeps the actor 'triage' instead of 'operator'.
        await _triage.RecordActionAsync(
            open.Id, FailureTriageActions.TriageEscalateModel, FailureTriageActors.Triage,
            DateTime.UtcNow, FailureTriageOutcomes.Pending, ct);

        // The marker is what the dispatch path consumes (single-shot)
        // to route the next run onto the escalation model.
        await _escalation.Markers.WriteAsync(_escalation.ProjectId, taskId, note, ct);

        var meta = new Dictionary<string, object>
        {
            // Same stale-error/fossil-head clearing as a triage
            // requeue — the task goes back to Pending and strike
            // counters deliberately survive (escalation SPENDS a
            // round; it counts toward the 2/day/task triage cap).
            ["lastError"] = null!,
            ["lastErrorAt"] = null!,
            ["reworkForSha"] = null!,
            ["reworkReason"] = $"triage-escalate: {signature}",
            ["reworkContext"] = $"This run has been escalated to a stronger model by the failure-triage agent. Triage note: {note}",
            ["requeuedFromFailedAt"] = DateTime.UtcNow.ToString("O"),
            ["triageAction"] = "escalate",
            ["triageNote"] = note,
            ["triageActionAt"] = DateTime.UtcNow.ToString("O"),
        };
        if (task.GetMetadata("prNumber") is not null)
            meta["prOpenedAt"] = DateTime.UtcNow.ToString("O");
        await _issues.TransitionAsync(taskId, IssueStatus.Pending,
            $"triage escalate to the role's escalation model ({signature})", meta, ct);

        // Same lifecycle repair as the requeue: without the machine
        // report the state stays Failed and the next dispatch violates.
        if (_lifecycle is not null)
        {
            var requeued = await _issues.GetAsync(taskId, ct);
            if (requeued is not null)
                await _lifecycle.ReportAsync(_issues, requeued, TaskEvent.OperatorRequeue,
                    watch: null, hasActiveDevRun: false, ct);
        }
        _logger.LogInformation(
            "Triage: escalated {TaskId} to the role's escalation model {Provider}/{Model} (signature {Signature})",
            taskId, target.Value.Provider.Name, target.Value.Model, signature);
        return $"ok: escalated {taskId} — the next dev run rides the role's escalation model ({target.Value.Provider.Name}/{target.Value.Model}); the failure ledger records actor=triage";
    }

    /// <summary>Park the task for the operator: no status change (it
    /// stays Failed/Blocked), the ledger row closes with the park
    /// action, and the task metadata says why — loudly.</summary>
    public async Task<string> ParkForOperatorAsync(string taskId, string reason, CancellationToken ct = default)
    {
        var task = await _issues.GetAsync(taskId, ct);
        if (task is null) return $"error: task {taskId} not found";
        var open = await _triage.GetOpenForTaskAsync(taskId, ct);
        if (open is null) return $"error: task {taskId} has no open ledger row — nothing to action";
        if (open.Action is not null) return $"error: task {taskId}'s ledger row is already actioned ({open.Action})";

        await _triage.RecordActionAsync(
            open.Id, FailureTriageActions.TriagePark, FailureTriageActors.Triage,
            DateTime.UtcNow, outcome: null, ct);
        await _issues.TransitionAsync(taskId, task.Status,
            "triage parked for operator", new Dictionary<string, object>
            {
                ["triageAction"] = "parked",
                ["triageNote"] = reason,
                ["triageActionAt"] = DateTime.UtcNow.ToString("O"),
            }, ct);
        _logger.LogInformation("Triage: parked {TaskId} for operator: {Reason}", taskId, reason);
        return $"ok: parked {taskId} for the operator — no further triage actions on this failure";
    }

    /// <summary>Flag the failure signature as a suspected product bug.
    /// Ledger flag + task metadata ONLY — the operator constraint is no
    /// automatic issue creation (phase 4, if directed).</summary>
    public async Task<string> FlagBugSuspectAsync(
        string taskId, string signature, string evidence, CancellationToken ct = default)
    {
        var task = await _issues.GetAsync(taskId, ct);
        if (task is null) return $"error: task {taskId} not found";
        var open = await _triage.GetOpenForTaskAsync(taskId, ct);
        if (open is null) return $"error: task {taskId} has no open ledger row — nothing to action";
        if (open.Action is not null) return $"error: task {taskId}'s ledger row is already actioned ({open.Action})";

        await _triage.RecordActionAsync(
            open.Id, FailureTriageActions.TriageFlagBug, FailureTriageActors.Triage,
            DateTime.UtcNow, outcome: null, ct);
        await _issues.TransitionAsync(taskId, task.Status,
            "triage flagged bug suspect", new Dictionary<string, object>
            {
                ["triageAction"] = "flag-bug",
                ["triageNote"] = evidence,
                ["triageBugSignature"] = signature,
                ["triageActionAt"] = DateTime.UtcNow.ToString("O"),
            }, ct);
        _logger.LogInformation("Triage: flagged bug suspect on {TaskId} (signature {Signature})", taskId, signature);
        return $"ok: flagged {taskId} as a bug suspect (signature {signature}) — the operator decides what happens next";
    }
}

/// <summary>Everything <see cref="TriageTools.EscalateModelAsync"/>
/// needs: the marker store (primary MemoryStore-backed, same store as
/// the role overrides), the LLM config + role overrides for
/// escalation-target resolution, and the project the markers +
/// project-scoped overrides resolve against. Null on paths that never
/// escalate (the deterministic guardrail park).</summary>
public sealed record TriageEscalationContext(
    TaskModelEscalations Markers,
    LlmConfig Config,
    RoleModelOverrides? Overrides,
    string ProjectId);
