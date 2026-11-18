using System.Text.Json;

namespace Forge.Core.Completion;

public sealed partial class SprintCompletionStore
{
    public async Task AbandonAsync(string id, string planHash, string reason, string authenticatedOperator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000 || string.IsNullOrWhiteSpace(authenticatedOperator)
            || authenticatedOperator.Length > 128)
            throw new ArgumentException("An explicit bounded interruption reason and authenticated provenance are required.");
        await using var connection = await issues.Db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        await LockAsync(connection, tx, ct);
        var receipt = await GetAsync(connection, tx, id, ct) ?? throw new InvalidOperationException("Completion not found.");
        if (receipt.PlanHash != planHash || receipt.PlanAuditId is null
            || receipt.EngineeringOutcome is not (EngineeringOutcome.Incomplete or EngineeringOutcome.PendingEvidence))
            throw new InvalidOperationException("Only the exact unresolved typed outcome may be abandoned.");
        if (receipt.AbandonmentJson is { } recorded)
        {
            var prior = JsonSerializer.Deserialize<CompletionAbandonment>(recorded)!;
            if (prior.PlanHash != planHash || prior.Reason != reason || prior.OperatorIdentity != authenticatedOperator)
                throw new InvalidOperationException("The interruption record is immutable.");
            return;
        }
        await ReadBoundPlanAsync(connection, tx, receipt, ct);
        using (var running = Command(connection, tx, $"SELECT COUNT(*) FROM {T("completion_triage")} WHERE completion_id=@id AND state='Running'", ("@id", id)))
            if (Convert.ToInt32(await running.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("Wait for active triage to finish before abandoning; uncertain requests require recovery review.");
        if (receipt.DeploymentCandidateId is { } candidate)
        {
            using var deployment = Command(connection, tx, $"SELECT status FROM {T("deployment")} WHERE id=@id AND completion_id=@completion",
                ("@id", candidate), ("@completion", receipt.Id));
            var status = await deployment.ExecuteScalarAsync(ct) as string;
            if (status is not ("Deployed" or "DeployFailed" or "BuildFailed" or "Rejected"))
                throw new InvalidOperationException("Stop or reject the linked deployment first, or wait until its independent release terminates.");
        }
        var abandonment = new CompletionAbandonment(planHash, reason, authenticatedOperator, DateTimeOffset.UtcNow);
        using var update = Command(connection, tx,
            $"UPDATE {T("sprint_completion")} SET abandonment_json=@json,ready_for_deployment=0,bookkeeping_state='Stopped',delivery_note='Outcome explicitly abandoned by operator; not delivered.',updated_at=@now WHERE id=@id",
            ("@json", JsonSerializer.Serialize(abandonment)), ("@now", Now()), ("@id", id));
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        await PublishOutcomeChangedAsync(receipt, "Abandoned", ct);
    }
}
