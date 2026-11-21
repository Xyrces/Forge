using System.Data.Common;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Existing active acceptance is authoritative when grooming a new physical prerequisite.</summary>
public sealed class ActivePrerequisiteGrooming(IssueStore issues)
{
    public async Task<SprintPlan?> ReadPlanAsync(string taskId, CancellationToken ct = default)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        return await ReadPlanAsync(connection, null, taskId, ct);
    }

    internal async Task<SprintPlan?> ReadPlanAsync(DbConnection connection, DbTransaction? tx, string taskId, CancellationToken ct)
    {
        var d = issues.Db.Dialect;
        using var read = connection.CreateCommand(); read.Transaction = tx;
        read.CommandText = $"SELECT {d.Top(2)}s.id FROM {d.Table("sprint")} s WHERE s.status='Active' AND EXISTS(SELECT 1 FROM {d.Table("sprint_issue")} m JOIN {d.Table("issue_dep")} e ON e.blocked_id=m.issue_id WHERE m.sprint_id=s.id AND e.blocker_id=@task AND e.kind='blocks'){d.Limit(2)}";
        read.AddParam("@task", taskId);
        var ids = new List<string>();
        await using (var r = await read.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        if (ids.Count == 0) return null;
        if (ids.Count != 1) throw new InvalidOperationException("Prerequisite has ambiguous active sprint ownership.");
        using var audit = connection.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = $"SELECT {d.Top(1)}weights_json FROM {d.Table("sprint_proposal_audit")} WHERE committed_sprint_id=@sprint ORDER BY id DESC{d.Limit(1)}";
        audit.AddParam("@sprint", ids[0]);
        var plan = await audit.ExecuteScalarAsync(ct) is string json ? SprintPlanJson.ReadAuditPlan(json) : null;
        if (plan is null || plan.ProjectId != issues.ProjectId)
            throw new InvalidOperationException("Active prerequisite needs an intact owning-project committed plan.");
        if (plan.Members.Any(m => m.TaskId == taskId)) return null;
        // A visibility-only target must not confer approved scope on another task.
        using var edges = connection.CreateCommand(); edges.Transaction = tx;
        edges.CommandText = $"SELECT blocked_id FROM {d.Table("issue_dep")} WHERE blocker_id=@task AND kind='blocks'";
        edges.AddParam("@task", taskId);
        await using var targets = await edges.ExecuteReaderAsync(ct);
        while (await targets.ReadAsync(ct))
            if (plan.Members.Any(m => m.TaskId == targets.GetString(0))) return plan;
        throw new InvalidOperationException("Prerequisite does not block a committed active member.");
    }

    internal async Task ValidateMetadataAsync(DbConnection connection, DbTransaction tx, string taskId, string metadata, CancellationToken ct)
    {
        var plan = await ReadPlanAsync(connection, tx, taskId, ct);
        if (plan is null) return;
        using var document = JsonDocument.Parse(metadata);
        if (!document.RootElement.TryGetProperty(SprintPlanJson.MetadataKey, out var value))
            throw new InvalidOperationException("Active prerequisite requires an explicit mapping to committed acceptance.");
        var binding = value.ValueKind == JsonValueKind.String
            ? JsonSerializer.Deserialize<SprintOutcomeBinding>(value.GetString()!, SprintPlanJson.Options)
            : value.Deserialize<SprintOutcomeBinding>(SprintPlanJson.Options);
        if (binding is null) throw new InvalidOperationException("Active prerequisite mapping is invalid.");
        SprintOutcomeAuthority.ValidateShape(taskId, binding);
        if (binding.Criteria.Any(c => !plan.Criteria.Contains(c))
            || binding.CriterionIds.Any(id => !plan.Criteria.Any(c => c.Id == id))
            || binding.RequiredTaskIds.Any(id => id != taskId && !plan.Members.Any(m => m.TaskId == id)))
            throw new InvalidOperationException("Active prerequisite cannot expand acceptance: reuse exact committed criterion IDs, text, evidence kinds and scopes; do not invent a duplicate criterion or delivery unit.");
        var authority = new SprintOutcomeAuthority(issues);
        foreach (var scope in binding.Criteria.Select(c => c.Scope).Append(binding.Authority).Distinct())
            await authority.ValidateAsync(connection, tx, scope, ct);
    }
}
