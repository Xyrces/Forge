using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class SprintStore
{
    private async Task<bool> HasCommittedAuditAsync(DbConnection connection, DbTransaction transaction, string sprintId, CancellationToken ct)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {T("sprint_proposal_audit")} WHERE committed_sprint_id=@sprint";
        command.AddParam("@sprint", sprintId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private async Task PersistPlanAsync(DbConnection connection, DbTransaction transaction, string sprintId,
        SprintActivationSnapshot snapshot, CancellationToken ct)
    {
        // Structural-only snapshots remain for internal migration/test callers. All production
        // activation routes obtain a plan from the shared approved-scope validator.
        if (snapshot.Plan is not { } plan) return;
        await ImplementationEvidenceFormat.ValidateJsonAsync(connection, transaction, _issues.Db.Dialect,
            SprintPlanJson.Serialize(plan), ct);
        if (!plan.HasValidHash || plan.ProjectId != _issues.ProjectId || plan.Criteria.Count == 0
            || plan.Members.Select(m => m.TaskId).Distinct().Count() != plan.Members.Count
            || !plan.Members.Select(m => m.TaskId).Order(StringComparer.Ordinal).SequenceEqual(
                snapshot.TaskIds.Concat((snapshot.ExistingSprintMembers ?? []).Select(i => i.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The approved outcome plan does not match exact sprint membership.");
        var authority = new SprintOutcomeAuthority(_issues);
        foreach (var criterion in plan.Criteria)
            await authority.ValidateAsync(connection, transaction, criterion.Scope, ct);
        foreach (var issue in snapshot.Issues.Where(i => snapshot.TaskIds.Contains(i.Id)))
        {
            var binding = SprintPlanJson.ReadBinding(issue) ?? throw new InvalidOperationException("Missing approved task scope.");
            SprintOutcomeAuthority.ValidateShape(issue.Id, binding);
            await authority.ValidateAsync(connection, transaction, binding.Authority, ct);
            var member = plan.Members.Single(m => m.TaskId == issue.Id);
            if (member.BindingHash != SprintPlanJson.Hash(binding) || member.CriterionIds.Count == 0 || member.CriterionIds.Any(id => !binding.CriterionIds.Contains(id)
                || !plan.Criteria.Any(c => c.Id == id && binding.Criteria.Contains(c))))
                throw new InvalidOperationException("Task acceptance mapping differs from the approved plan.");
        }
        if (snapshot.ProposalAuditId is { } auditId)
        {
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = $"SELECT weights_json FROM {T("sprint_proposal_audit")} WHERE id=@id AND committed_sprint_id IS NULL";
            read.AddParam("@id", auditId);
            if (await read.ExecuteScalarAsync(ct) is not string json || SprintPlanJson.ReadAuditPlan(json)?.Hash != plan.Hash)
                throw new InvalidOperationException("Proposal plan changed or was already committed.");
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = $"UPDATE {T("sprint_proposal_audit")} SET committed_sprint_id=@sprint,committed_by=@who WHERE id=@id AND committed_sprint_id IS NULL";
            update.AddParam("@sprint", sprintId); update.AddParam("@who", snapshot.CommittedBy ?? "operator"); update.AddParam("@id", auditId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Proposal already committed.");
        }
        else
        {
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {T("sprint_proposal_audit")}(ts,theme,goal,weights_json,candidates_json,selected_task_ids_json,committed_sprint_id,committed_by) VALUES(@now,@goal,@goal,@weights,'[]',@ids,@sprint,@who)";
            insert.AddParam("@now", IssueStore.DateFormatTime(DateTime.UtcNow)); insert.AddParam("@goal", plan.Objective);
            insert.AddParam("@weights", SprintPlanJson.Serialize(new { plan }));
            insert.AddParam("@ids", JsonSerializer.Serialize(plan.Members.Select(m => m.TaskId)));
            insert.AddParam("@sprint", sprintId); insert.AddParam("@who", snapshot.CommittedBy ?? "automatic");
            await insert.ExecuteNonQueryAsync(ct);
        }
        using var goal = connection.CreateCommand(); goal.Transaction = transaction;
        goal.CommandText = $"UPDATE {T("sprint")} SET goal=@goal WHERE id=@id";
        goal.AddParam("@goal", plan.Objective); goal.AddParam("@id", sprintId);
        await goal.ExecuteNonQueryAsync(ct);
    }
}
