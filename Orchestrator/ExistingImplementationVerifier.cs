using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Agents;
using Forge.AgentTools;
using Forge.Core;
using Forge.Core.Completion;
using Forge.Orchestrator.Slots;
using Forge.Orchestrator.Workflow;
using Forge.Orchestrator.Workers;

namespace Forge.Orchestrator;

/// <summary>Independent acceptance of already-existing code; a model's no-op statement is only a request.</summary>
public sealed class ExistingImplementationVerifier(IssueStore issues, ISprintStore sprints,
    GitWorktreeService worktrees, GitHubService gitHub, IAgentRunner runner, SlotTable slots,
    string projectId, string defaultBranch)
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<bool> TryCompleteAsync(AgentCompleted input, IReadOnlyList<string> commands, CancellationToken ct)
    {
        if (!input.RequiresWorkerVerification || input.Verification is not { } verification
            || runner is not IReviewWorkerLifecycle lifecycle) return false;
        var task = await issues.GetAsync(input.Worktree.Claim.Issue.Id, ct)
            ?? throw new InvalidOperationException("Existing implementation task disappeared.");
        if (!string.IsNullOrWhiteSpace(task.GetMetadata("prNumber"))) return false; // Existing PRs remain owned by the normal watch loop.
        var sprint = await sprints.GetActiveAsync(ct);
        var plan = sprint is null ? null : await new SprintProposalAuditStore(issues.Db).GetPlanForSprintAsync(sprint.Id, ct);
        if (plan is null || plan.ProjectId != projectId || !plan.HasValidHash) return false;
        if (task.Status == IssueStatus.Completed)
            return ExistingImplementationEvidence.TryGet(task, plan, out var recorded) && recorded!.Verification == verification;
        var member = plan.Members.SingleOrDefault(m => m.TaskId == task.Id);
        if (member is null) return false;
        var criteria = plan.Criteria.Where(c => member.CriterionIds.Contains(c.Id)
            && c.EvidenceKind is SprintEvidenceKind.MergedAndVerified or SprintEvidenceKind.ImplementationVerified).ToArray();
        if (criteria.Length == 0 || criteria.Any(c => c.EvidenceKind != SprintEvidenceKind.ImplementationVerified)) return false;
        // Refuse before git/network/reviewer work on the bridge's pre-upgrade catalog.
        await using (var connection = await issues.Db.OpenAsync(ct))
            await ImplementationEvidenceFormat.RequireAsync(connection, null, issues.Db.Dialect, ct);
        var path = input.Worktree.WorktreePath ?? throw new InvalidOperationException("Existing implementation has no checkout.");
        var source = await ValidateSourceAsync(path, verification, commands, ct);
        var request = new ReviewRequest(task.Id, projectId, plan.Hash, source, verification.TreeSha,
            verification.CommandsSha, SprintPlanJson.Hash(criteria), criteria.Select(c => c.Id).ToArray());
        var dispatch = "existing-" + SprintPlanJson.Hash(new { verification.AttemptId, request });
        if (slots.MaxFor(projectId, "reviewer") < 1)
            throw new InvalidOperationException("Existing implementation verification requires a configured reviewer slot.");
        await using var slot = await slots.TryAcquireAsync(projectId, "reviewer", Reviewer.ReviewerDispatcher.ReviewRunTimeout, ct);
        ct.ThrowIfCancellationRequested();
        if (slot is null) throw new InvalidOperationException("Existing implementation reviewer slot is unavailable.");
        var context = new Dictionary<string, object>
        {
            ["projectId"] = projectId, ["issueId"] = task.Id, ["worktreePath"] = path,
            ["sourceSha"] = source, ["dispatchId"] = dispatch, ["existingImplementationReview"] = "true",
        };
        var prompt = "Independently verify that the existing implementation at the EXACT source below satisfies EVERY listed engineering criterion. " +
            "Inspect the checkout and relevant tests; the engineer's NO_CHANGES_NEEDED assertion is not evidence. " +
            "No PR is being reviewed. Do not attest deployment or operator observations. Do not edit files. " +
            "Return only one JSON object with all request identity fields unchanged, verdict (Approve or RequestChanges), and criterionReviews (one object per criterion with id, verdict and evidence) " +
            "with concrete file:line evidence for each criterion. Approve only when all criteria are established.\n" +
            "Request identity:\n" + JsonSerializer.Serialize(request, ResponseJson) +
            "\nExact approved criteria and scope:\n" + SprintPlanJson.Serialize(criteria);
        // An earlier logical review may still own this checkout. Only this exact
        // invocation may be adopted; a different live invocation must be reconciled.
        var prior = await InspectAsync(lifecycle, task.Id, dispatch, ct);
        if (prior is null)
        {
            try
            {
                if (await lifecycle.HasUntrackedReviewWorkerAsync(AgentType.Reviewer, projectId, task.Id, ct))
                    throw new WorkerExecutionUncertainException("Another reviewer still owns the existing-implementation checkout.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new WorkerExecutionUncertainException("Prior reviewer termination is unconfirmed; preserve the checkout.", ex); }
        }
        AgentRunResult result;
        try { result = await runner.RunAsync(AgentType.Reviewer, prompt, null, context, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var state = await InspectAsync(lifecycle, task.Id, dispatch, ct);
            if (state is { Terminal: false })
                throw new WorkerExecutionUncertainException("Independent reviewer is still active; preserve the checkout for adoption.", ex);
            throw;
        }
        ct.ThrowIfCancellationRequested();
        var worker = await InspectAsync(lifecycle, task.Id, dispatch, ct);
        if (worker is not { Terminal: true } || string.IsNullOrWhiteSpace(worker.AttemptId)
            || worker.AttemptId == verification.AttemptId)
            throw new WorkerExecutionUncertainException("Independent reviewer worker has no joined durable result identity.");
        ReviewResponse response;
        try { response = JsonSerializer.Deserialize<ReviewResponse>(result.Text, ResponseJson)
                ?? throw new JsonException("Empty structured review."); }
        catch (JsonException ex) { throw new InvalidOperationException("Existing implementation review did not return a valid structured verdict.", ex); }
        if (response.TaskId != request.TaskId || response.ProjectId != request.ProjectId || response.PlanHash != request.PlanHash
            || response.SourceSha != request.SourceSha || response.TreeSha != request.TreeSha || response.CommandsSha != request.CommandsSha
            || response.CriteriaHash != request.CriteriaHash || response.CriterionIds is null
            || response.CriterionIds.Count != request.CriterionIds.Count
            || !request.CriterionIds.ToHashSet(StringComparer.Ordinal).SetEquals(response.CriterionIds)
            || response.CriterionReviews is null || response.CriterionReviews.Count != criteria.Length
            || response.CriterionReviews.Any(c => c is null)
            || !request.CriterionIds.ToHashSet(StringComparer.Ordinal).SetEquals(response.CriterionReviews.Select(c => c.Id))
            || response.CriterionReviews.Any(c => string.IsNullOrWhiteSpace(c.Evidence) || c.Evidence.Length > 4000))
            throw new InvalidOperationException("Existing implementation review does not cover the exact approved request.");
        if (response.Verdict != "Approve" || response.CriterionReviews.Any(c => c.Verdict != "Approve"))
            throw new InvalidOperationException("Independent existing implementation review did not approve: " +
                string.Join("; ", response.CriterionReviews.Select(c => c.Id + ": " + c.Evidence)));
        if (await ValidateSourceAsync(path, verification, commands, ct) != source)
            throw new InvalidOperationException("Configured source changed during existing implementation review.");
        var review = new ExistingImplementationReview(task.Id, projectId, plan.Hash, source,
            verification.TreeSha, verification.CommandsSha, request.CriterionIds, "Approve", worker.AttemptId, DateTimeOffset.UtcNow);
        await issues.CompleteVerifiedExistingAsync(task.Id, task.UpdatedAt, task.MetadataJson,
            new(task.Id, projectId, plan.Hash, source, criteria, verification, review), ct);
        return true;
    }

    private async Task<ReviewWorkerState?> InspectAsync(IReviewWorkerLifecycle lifecycle, string taskId, string dispatch, CancellationToken ct)
    {
        try { return await lifecycle.InspectReviewWorkerAsync(AgentType.Reviewer, projectId, taskId, dispatch, false, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { throw new WorkerExecutionUncertainException("Independent reviewer ownership/termination is uncertain; preserve the checkout.", ex); }
    }

    private async Task<string> ValidateSourceAsync(string path, VerificationReceipt verification,
        IReadOnlyList<string> commands, CancellationToken ct)
    {
        var source = await gitHub.GetBranchHeadShaAsync(defaultBranch, ct);
        if (source.Length != 40 || source.Any(c => !char.IsAsciiHexDigit(c))
            || source != await worktrees.GetHeadShaAsync(path, ct)
            || verification.TreeSha != await worktrees.GetCommitTreeShaAsync(path, source, ct)
            || !await WorkerVerification.MatchesAsync(verification, path, commands, ct, worktrees.WorkspaceRoot))
            throw new InvalidOperationException("Existing implementation must match the fresh configured source and trusted worker verification exactly.");
        return source;
    }

    public sealed record ReviewRequest(string TaskId, string ProjectId, string PlanHash, string SourceSha,
        string TreeSha, string CommandsSha, string CriteriaHash, IReadOnlyList<string> CriterionIds);
    public sealed record ReviewResponse(string TaskId, string ProjectId, string PlanHash, string SourceSha,
        string TreeSha, string CommandsSha, string CriteriaHash, IReadOnlyList<string> CriterionIds,
        string Verdict, IReadOnlyList<CriterionReview> CriterionReviews);
    public sealed record CriterionReview(string Id, string Verdict, string Evidence);
}
