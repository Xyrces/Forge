using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

internal static class CompletionPlanningBarrier
{
    internal static string BookkeepingReadyPredicate(ISqlDialect dialect)
    {
        var completion = dialect.Table("sprint_completion");
        return $"(bookkeeping_state='Completed' AND NOT EXISTS(SELECT 1 FROM {dialect.Table("completion_triage")} b WHERE b.completion_id={completion}.id AND b.state <> 'Completed') AND NOT EXISTS(SELECT 1 FROM {dialect.Table("followup_draft")} f WHERE f.consumed_at IS NULL AND NOT EXISTS(SELECT 1 FROM {dialect.Table("completion_draft_claim")} claim WHERE claim.draft_id=f.id) AND (f.sprint_id={completion}.sprint_id OR EXISTS(SELECT 1 FROM {dialect.Table("sprint_issue")} member WHERE member.sprint_id={completion}.sprint_id AND member.issue_id=f.source_issue_id))))";
    }

    internal static string Predicate(ISqlDialect dialect, bool includeLegacyIncomplete = false)
    {
        var completion = dialect.Table("sprint_completion");
        var unfinishedBookkeeping = includeLegacyIncomplete
            ? "bookkeeping_state <> 'Completed'"
            : $"EXISTS(SELECT 1 FROM {dialect.Table("completion_triage")} b WHERE b.completion_id={completion}.id AND b.state IN ('Pending','Running','Decided')) OR EXISTS(SELECT 1 FROM {dialect.Table("followup_draft")} f WHERE f.consumed_at IS NULL AND NOT EXISTS(SELECT 1 FROM {dialect.Table("completion_draft_claim")} claim WHERE claim.draft_id=f.id) AND (f.sprint_id={completion}.sprint_id OR EXISTS(SELECT 1 FROM {dialect.Table("sprint_issue")} member WHERE member.sprint_id={completion}.sprint_id AND member.issue_id=f.source_issue_id)))";
        return $"(abandonment_json IS NULL AND (((completion_policy_json IS NULL OR outcome <> 'Satisfied') AND ({unfinishedBookkeeping})) OR ({(includeLegacyIncomplete ? "1=1" : "plan_audit_id IS NOT NULL")} AND outcome IN ('PendingEvidence','Incomplete')) OR (delivery_required=1 AND ready_for_deployment=1 AND NOT EXISTS(SELECT 1 FROM {dialect.Table("deployment")} d WHERE d.id={completion}.deployment_candidate_id AND d.completion_id={completion}.id AND d.commit_sha={completion}.source_sha AND d.status='Deployed' AND d.receipt_json IS NOT NULL))))";
    }
}

public sealed partial class SprintStore
{
    internal async Task EnsurePriorCompletionResolvedAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var dialect = _issues.Db.Dialect;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {dialect.Top(1)}id FROM {T("sprint_completion")} WHERE {CompletionPlanningBarrier.Predicate(dialect)}{dialect.Limit(1)}";
        if (await command.ExecuteScalarAsync(ct) is string id)
            throw new InvalidOperationException($"Prior sprint completion {id} still requires bookkeeping or delivery evidence; explicitly abandon its outcome to interrupt.");
    }
}
