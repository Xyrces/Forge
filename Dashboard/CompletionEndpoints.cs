using Forge.Core;
using Forge.Core.Completion;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

public sealed record CompletionBatchSummary(string Id, string State, string? Error);
public sealed record CompletionDeploymentApproval(string PlanHash);
public sealed record AbandonCompletionRequest(string PlanHash, string Reason);
public sealed record CompletionDetail(CompletionOverview Receipt, IReadOnlyList<CompletionBatchSummary> Batches,
    string? NextCursor, IReadOnlyList<CompletionCriterionStatus> Criteria);

public static class CompletionEndpoints
{
    public static void MapCompletionEndpoints(WebApplication app, IIssueStore primary, ProjectContextFactory? projects,
        Func<CancellationToken, Task<string?>>? runningRevision = null)
    {
        IssueStore? Resolve(string projectId) => projects is null
            ? primary is IssueStore store && projectId == store.ProjectId ? store : null
            : projects.Find(projectId)?.Issues as IssueStore;

        app.MapGet("/api/sprints/{sprintId}/completion-policy", async (string sprintId, string projectId, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            return Results.Ok(new { policy = await new SprintCompletionStore(issues).ReadSprintPolicyAsync(sprintId, ct) });
        });
        app.MapGet("/api/completions", async (string projectId, int? limit, string? cursor, bool? waitingOnly, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            var store = new SprintCompletionStore(issues);
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var rows = await store.ListOverviewAsync(take, cursor, waitingOnly ?? true, ct: ct);
            return Results.Ok(new { items = rows, nextCursor = rows.Count == take ? rows[^1].Id : null });
        });
        app.MapGet("/api/sprints/{sprintId}/completion", async (string sprintId, string projectId, string? cursor, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            var store = new SprintCompletionStore(issues);
            var receipt = await store.GetAsync("completion-" + sprintId, ct);
            if (receipt is null) return Results.NotFound();
            var batches = await store.ListBatchesAsync(receipt.Id, ct, limit: 20, afterId: cursor);
            return Results.Ok(new CompletionDetail((await store.ListOverviewAsync(1, waitingOnly: false, completionId: receipt.Id, ct: ct)).Single(),
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
            if (runningRevision is null || !string.Equals(await runningRevision(ct), evidence.SourceSha, StringComparison.Ordinal))
                return Results.Conflict(new { error = "The running dashboard revision does not match the observed deployed source." });
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
        app.MapPost("/api/sprints/{sprintId}/completion/approve-deployment", async (string sprintId, string projectId,
            CompletionDeploymentApproval request, HttpContext context, CancellationToken ct) =>
        {
            if (context.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string identity) return Results.Unauthorized();
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            try
            {
                await new SprintCompletionStore(issues).ApproveDeploymentAsync("completion-" + sprintId, request.PlanHash, identity, ct);
                return Results.Ok(new { message = "Configured deployment gate approved for this exact completion." });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/sprints/{sprintId}/completion/reconcile-delivered", async (string sprintId, string projectId,
            ReconcileDeliveredCompletionRequest request, HttpContext context, CancellationToken ct) =>
        {
            if (context.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string identity) return Results.Unauthorized();
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            try
            {
                await new SprintCompletionStore(issues).ReconcileDeliveredAsync("completion-" + sprintId, request, identity, ct);
                return Results.Ok(new { message = "Delivered completion closed with missed criteria recorded as an explicit exception; no observation fabricated." });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/sprints/{sprintId}/completion/abandon", async (string sprintId, string projectId,
            AbandonCompletionRequest request, HttpContext context, CancellationToken ct) =>
        {
            if (context.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string identity)
                return Results.Unauthorized();
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound();
            try
            {
                await new SprintCompletionStore(issues).AbandonAsync("completion-" + sprintId, request.PlanHash, request.Reason, identity, ct);
                return Results.Ok(new { message = "Outcome explicitly abandoned; no delivery success was recorded." });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

}
