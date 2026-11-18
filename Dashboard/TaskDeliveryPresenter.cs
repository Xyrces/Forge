using System.Text.Json;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Dashboard.Features.Tasks;

namespace Forge.Dashboard;

internal static class TaskDeliveryPresenter
{
    internal static TaskDeliveryRead For(IssueRecord task, SprintPlan? plan = null)
    {
        var binding = SprintPlanJson.ReadBinding(task);
        IReadOnlyList<TaskScopeCriterion> criteria = [];
        if (binding is { Version: 1, Criteria: not null, RequiredTaskIds: not null, CriterionIds: not null }
            && binding.RequiredTaskIds.Contains(task.Id))
        {
            criteria = binding.Criteria.Where(criterion => criterion is not null
                    && binding.CriterionIds.Contains(criterion.Id) && !string.IsNullOrWhiteSpace(criterion.Text))
                .Select(criterion => new TaskScopeCriterion(criterion.Text, criterion.EvidenceKind.ToString())).ToArray();
        }
        var merge = task.Status == IssueStatus.Completed && TaskSituation.HasObservedMerge(task)
            ? JsonSerializer.Deserialize<ObservedMergeEvidence>(task.GetMetadata("observedMerge")!) : null;
        var publication = TaskDeliveryEvidence.IsMergedAndVerified(task)
            ? JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication")!) : null;
        var existing = plan is not null && ExistingImplementationEvidence.TryGet(task, plan, out var verified) ? verified : null;
        return new(criteria, publication?.PublishedHeadSha, publication?.Verification.CompletedAt,
            merge?.MergeSha, merge?.ObservedAt, existing?.SourceSha, existing?.Verification.CompletedAt, existing?.Review.ReviewedAt);
    }
}
