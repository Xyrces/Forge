using Forge.Core;
using Forge.Core.Initiatives;
using Forge.Configuration;
using Forge.Orchestrator;
using Forge.Agents;
using Xunit;

namespace Forge.Tests;

internal static class ApprovedSprintFixture
{
    internal static async Task<SprintScopeReference> Scope(IssueStore issues, string key = "scope")
    {
        var proposals = new InitiativeProposalStore(issues);
        var draft = await proposals.CreateAsync(new(issues.ProjectId, key + Guid.NewGuid(), "forge", "Approved useful work", "Deliver observable accepted behavior and its necessary remediation.", 2));
        await proposals.ExpandInIntakeAsync(issues.ProjectId, draft.Id);
        var approved = await proposals.ApproveAsync(issues.ProjectId, draft.Id, 1, draft.Hash, "test operator");
        var ingestion = new InitiativeIngestionStore(issues);
        var receipt = await ingestion.IngestAsync(approved);
        await ingestion.TryBeginRefinementAsync(approved.Id, 1);
        await ingestion.CompleteRefinementAsync(approved.Id, 1);
        return new(receipt.SpecId, approved.Id, 1, approved.Hash, approved.Body);
    }
    internal static async Task Bind(IssueStore issues, SprintScopeReference scope, IReadOnlyList<IssueRecord> tasks,
        string outcome = "goal", string criterion = "behavior", string objective = "Useful approved outcome")
    {
        var criteria = new[] { new SprintCriterion(criterion, "Deliver " + criterion, SprintEvidenceKind.MergedAndVerified, scope) };
        foreach (var task in tasks)
            await new SprintOutcomeAuthority(issues).BindAsync(task.Id, new(1, outcome, objective, criteria,
                tasks.Select(t => t.Id).ToArray(), [criterion], SprintMemberReason.Delivers, scope));
    }
    internal static Task<IssueRecord> Task(IssueStore issues, bool groomed = true) => issues.CreateAsync(new("task", "Work", Metadata: new Dictionary<string, object> { ["groomed"] = groomed }));
}
