using Forge.Core;
using Forge.Core.Completion;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

public sealed record CompletionSummary(string Id, string ProjectId, string SprintId, string Outcome,
    string BookkeepingState, bool ReadyForDeployment, string? SourceSha, string? DeploymentCandidateId,
    string? DeploymentStatus, string? DeliveryNote, string? PlanHash);
public sealed record CompletionBatchSummary(string Id, string State, string? Error);
public sealed record CompletionDetail(CompletionSummary Receipt, IReadOnlyList<CompletionBatchSummary> Batches,
    string? NextCursor, IReadOnlyList<CompletionCriterionStatus> Criteria);

public static class CompletionEndpoints
{
    public static void MapCompletionEndpoints(WebApplication app, IIssueStore primary, ProjectContextFactory? projects)
    {
        IssueStore? Resolve(string projectId) => projects is null
            ? primary is IssueStore store && projectId == store.ProjectId ? store : null
            : projects.Find(projectId)?.Issues as IssueStore;

        app.MapGet("/api/completions", async (string projectId, int? limit, string? cursor, bool? waitingOnly, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            var store = new SprintCompletionStore(issues);
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var rows = await store.ListVisibleAsync(take, cursor, ct, waitingOnly ?? true);
            var summaries = new List<CompletionSummary>();
            foreach (var row in rows) summaries.Add(await Summary(store, row, ct));
            return Results.Ok(new { items = summaries, nextCursor = rows.Count == take ? rows[^1].Id : null });
        });
        app.MapGet("/api/sprints/{sprintId}/completion", async (string sprintId, string projectId, string? cursor, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            var store = new SprintCompletionStore(issues);
            var receipt = await store.GetAsync("completion-" + sprintId, ct);
            if (receipt is null) return Results.NotFound();
            var batches = await store.ListBatchesAsync(receipt.Id, ct, limit: 20, afterId: cursor);
            return Results.Ok(new CompletionDetail(await Summary(store, receipt, ct),
                batches.Select(b => new CompletionBatchSummary(b.Id, b.State, b.Error)).ToArray(),
                batches.Count == 20 ? batches[^1].Id : null, await store.ReadCriteriaAsync(receipt.Id, ct)));
        });
        app.MapPost("/api/sprints/{sprintId}/completion/triage/{batchId}/retry", async (string sprintId,
            string batchId, string projectId, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            var store = new SprintCompletionStore(issues);
            var receipt = await store.GetAsync("completion-" + sprintId, ct);
            if (receipt is null) return Results.NotFound();
            return await store.RetryTriageAsync(batchId, ct, receipt.Id)
                ? Results.Ok(new { state = "Pending", message = "Explicit retry queued; a new model request may be charged." })
                : Results.Conflict(new { error = "Only a NeedsReview batch belonging to this completion can be retried." });
        });
        app.MapPost("/api/sprints/{sprintId}/completion/operator-evidence", async (string sprintId,
            string projectId, OperatorCriterionEvidence evidence, HttpContext context, CancellationToken ct) =>
        {
            if (context.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string identity)
                return Results.Unauthorized();
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            var store = new SprintCompletionStore(issues);
            try
            {
                await store.RecordOperatorEvidenceAsync("completion-" + sprintId, evidence, identity, ct);
                await store.RefreshDeploymentOutcomeAsync("completion-" + sprintId, ct);
                return Results.Ok(new { message = "Explicit operator observation recorded for the exact deployed source." });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    private static async Task<CompletionSummary> Summary(SprintCompletionStore store, SprintCompletionRecord receipt, CancellationToken ct)
        => new(receipt.Id, receipt.ProjectId, receipt.SprintId, receipt.EngineeringOutcome.ToString(),
            receipt.BookkeepingState, receipt.ReadyForDeployment, receipt.SourceSha, receipt.DeploymentCandidateId,
            await store.GetDeploymentStatusAsync(receipt.Id, ct), receipt.DeliveryNote, receipt.PlanHash);
}
