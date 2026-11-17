using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Exact approved-content bindings; grooming may describe scope but cannot create approval.</summary>
public sealed class SprintOutcomeAuthority(IssueStore issues)
{
    public async Task ValidateAsync(SprintScopeReference scope, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        await ValidateAsync(connection, null, scope, ct);
    }

    internal async Task ValidateAsync(DbConnection connection, DbTransaction? transaction, SprintScopeReference scope, CancellationToken ct)
    {
        if (scope is null || scope.Revision < 1 || string.IsNullOrWhiteSpace(scope.ApprovedExcerpt) || scope.ApprovedExcerpt.Length > 4000)
            throw new InvalidOperationException("An exact approved scope excerpt is required.");
        var d = issues.Db.Dialect;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT r.project_id,r.proposal_id,r.revision,r.content_hash,r.refinement_state,e.title,e.description,e.priority FROM {d.Table("initiative_ingestion")} r JOIN {d.Table("issue")} e ON e.id=r.epic_id WHERE r.spec_id=@spec";
        command.AddParam("@spec", scope.SpecId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Approved initiative mapping is missing.");
        var project = reader.GetString(0); var title = reader.GetString(5); var body = reader.GetString(6); var priority = reader.GetInt32(7);
        if (project != issues.ProjectId || reader.GetString(1) != scope.ProposalId || reader.GetInt32(2) != scope.Revision
            || reader.GetString(3) != scope.Hash || reader.GetString(4) != "Completed"
            || Initiatives.InitiativeDb.Hash(project, title, body, priority) != scope.Hash
            || !body.Contains(scope.ApprovedExcerpt, StringComparison.Ordinal))
            throw new InvalidOperationException("Outcome scope differs from the intact approved initiative or refinement is incomplete.");
    }

    public static void ValidateShape(string taskId, SprintOutcomeBinding binding)
    {
        if (binding is null || binding.RequiredTaskIds is null || binding.Criteria is null || binding.CriterionIds is null || binding.Authority is null
            || binding.Version != 1 || string.IsNullOrWhiteSpace(binding.OutcomeId) || binding.OutcomeId.Length > 128
            || string.IsNullOrWhiteSpace(binding.Objective) || binding.Objective.Length > 1000
            || binding.RequiredTaskIds.Count is < 1 or > 1000 || !binding.RequiredTaskIds.Contains(taskId)
            || binding.RequiredTaskIds.Distinct().Count() != binding.RequiredTaskIds.Count
            || binding.Criteria.Count is < 1 or > 20 || binding.Criteria.Any(c => c is null) || binding.Criteria.Select(c => c.Id).Distinct().Count() != binding.Criteria.Count
            || binding.CriterionIds.Count == 0 || binding.CriterionIds.Any(id => !binding.Criteria.Any(c => c.Id == id))
            || !Enum.IsDefined(binding.Reason)
            || binding.Criteria.Any(c => c is null || c.Scope is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 128 || string.IsNullOrWhiteSpace(c.Text)
                || c.Text.Length > 2000 || !Enum.IsDefined(c.EvidenceKind)))
            throw new InvalidOperationException("Malformed explicit outcome mapping.");

        if (!binding.Criteria.Any(c => binding.CriterionIds.Contains(c.Id)
                && c.EvidenceKind == SprintEvidenceKind.MergedAndVerified))
            throw new InvalidOperationException(
                "Every engineering task must contribute to a genuine pre-deployment MergedAndVerified criterion. "
                + "Attach DeploymentHealthy and OperatorObserved criteria to the implementation delivery members in the same outcome, "
                + "not to separate observation-only engineering tasks or sprints. Do not invent code changes or PRs to satisfy this requirement; "
                + "post-deployment evidence still requires actual runtime or operator verification.");
    }

    public async Task BindAsync(string taskId, SprintOutcomeBinding binding, CancellationToken ct = default)
    {
        ValidateShape(taskId, binding);
        foreach (var scope in binding.Criteria.Select(c => c.Scope).Append(binding.Authority).Distinct()) await ValidateAsync(scope, ct);
        var issue = await issues.GetAsync(taskId, ct) ?? throw new InvalidOperationException("Task not found.");
        if (issue.Status != IssueStatus.Pending || AgentTaskTypes.IsContainer(issue.Type))
            throw new InvalidOperationException("Only pending engineering work can receive a grooming outcome mapping.");
        await issues.MergePendingMetadataAsync(taskId, new Dictionary<string, object> {
            [SprintPlanJson.MetadataKey] = JsonSerializer.SerializeToElement(binding, SprintPlanJson.Options) }, ct);
    }
}
