using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Bounded owning-project decomposition for safe explicit grooming resumption.</summary>
public sealed class GroomingDecompositionStore(IssueStore issues, ISpecStore? specs = null)
{
    public async Task<IReadOnlyList<IssueRecord>> ReadPendingAsync(string specId, CancellationToken ct = default, bool allowFinishedHistory = false)
    {
        var spec = await (specs ?? new SpecStore(issues)).GetAsync(specId, ct);
        if (spec is null || spec.ProjectId != issues.ProjectId)
            throw new InvalidOperationException("Grooming decomposition belongs to another project or specification.");
        var d = issues.Db.Dialect;
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {d.Top(101)}id,short_id,type,title,description,status,priority,assignee,created_at,updated_at,closed_at,metadata_json,parent_issue_id,dispatch_checkpoint,checkpoint_at,recovery_attempts FROM {d.Table("issue")} WHERE parent_issue_id=@spec OR parent_issue_id IN(SELECT id FROM {d.Table("issue")} WHERE parent_issue_id=@spec AND type='story') ORDER BY created_at,id{d.Limit(101)}";
        command.AddParam("@spec", specId);
        var rows = new List<IssueRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(IssueStore.ReadIssue(reader));
        if (rows.Count > 100) throw new InvalidOperationException("Existing decomposition exceeds the 100-row recovery bound; inspect its scope before retrying.");
        var stories = rows.Where(r => r.Type == "story" && r.ParentIssueId == specId).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (rows.Any(r => (r.Status != IssueStatus.Pending && !(allowFinishedHistory && r.Status is IssueStatus.Completed or IssueStatus.Closed)) || (r.Type == "story" ? !stories.Contains(r.Id)
                : AgentTaskTypes.IsContainer(r.Type) || r.Type == AgentTaskTypes.PrWatch || r.ParentIssueId is null || !stories.Contains(r.ParentIssueId))))
            throw new InvalidOperationException("Only intact Pending story/task rows belonging to this exact specification can resume grooming.");
        return allowFinishedHistory ? rows.Where(r => r.Status == IssueStatus.Pending).ToArray() : rows;
    }
}
