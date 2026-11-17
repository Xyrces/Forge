using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>Engineering claim admission only; PR watch lifecycle never calls this guard.</summary>
internal static class SprintDispatchGuard
{
    public static async Task<string?> CheckAsync(IssueStore issues, ISprintStore sprints, string projectId, string taskId, CancellationToken ct)
    {
        var active = await sprints.GetActiveAsync(ct);
        if (active is null) return "No active approved sprint.";
        var plan = await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(active.Id, ct);
        if (plan is null || plan.ProjectId != projectId)
            return "Active sprint has no current approved outcome evidence; explicit scope repair is required before engineering resumes.";
        var member = plan.Members.SingleOrDefault(m => m.TaskId == taskId);
        if (member is null || !(await sprints.GetIssueIdsAsync(active.Id, ct)).Contains(taskId))
            return "Task is not a member of the current committed outcome plan.";
        var task = await issues.GetAsync(taskId, ct);
        if (task is null || task.Status != IssueStatus.Pending) return "Task is no longer pending for engineering claim.";
        var binding = SprintPlanJson.ReadBinding(task);
        if (binding is null) return "Task lacks its approved outcome mapping.";
        try
        {
            SprintOutcomeAuthority.ValidateShape(taskId, binding);
            if (member.BindingHash != SprintPlanJson.Hash(binding))
                return "Task grooming outcome changed after the committed plan; an audited scope amendment is required.";
            var cursor = task;
            var ancestry = new HashSet<string>(StringComparer.Ordinal) { taskId };
            var hasSpec = false;
            while (cursor.ParentIssueId is { } parentId)
            {
                if (ancestry.Count >= 100 || !ancestry.Add(parentId)) return "Task ancestry is cyclic or exceeds the validation bound.";
                if (await issues.GetAsync(parentId, ct) is { } parent) { cursor = parent; continue; }
                var spec = await new SpecStore(issues).GetAsync(parentId, ct);
                if (spec is null || spec.ProjectId != projectId || spec.Status != SpecStatus.Groomed || binding.Authority.SpecId != spec.Id)
                    return "The task's own approved specification is no longer technically groomed.";
                hasSpec = true; break;
            }
            if (!hasSpec && !string.Equals(task.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase))
                return "Task still requires technical grooming before engineering claim.";
            if (member.CriterionIds.Any(id => !binding.CriterionIds.Contains(id)
                || !plan.Criteria.Any(c => c.Id == id && binding.Criteria.Contains(c))))
                return "Task outcome mapping differs from the committed plan; grooming scope repair is required.";
            var authority = new SprintOutcomeAuthority(issues);
            foreach (var scope in plan.Criteria.Where(c => member.CriterionIds.Contains(c.Id)).Select(c => c.Scope).Append(binding.Authority).Distinct())
                await authority.ValidateAsync(scope, ct);
        }
        catch (InvalidOperationException ex) { return ex.Message; }
        return null;
    }
}
