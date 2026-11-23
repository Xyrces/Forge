using Forge.Core;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

public static class TaskWorkerDependencyEndpoints
{
    public sealed record DecisionRequest(string Status);

    public static void MapTaskWorkerDependencyEndpoints(WebApplication app, IIssueStore primary, ProjectContextFactory? projects)
    {
        IssueStore? Resolve(string? projectId) => string.IsNullOrWhiteSpace(projectId) ? null
            : projects is not null ? projects.Find(projectId)?.Issues as IssueStore
            : primary is IssueStore store && store.ProjectId == projectId ? store : null;
        app.MapGet("/api/tasks/{id}/worker-dependencies", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = Resolve(projectId);
            var task = store is null ? null : await store.GetAsync(id, ct);
            return task is null ? Results.NotFound() : Results.Json(WorkerDependencyRequests.Read(task));
        });
        // DashboardMutationAuthentication protects every API mutation with the operator token.
        app.MapPost("/api/tasks/{id}/worker-dependencies/{requestId}", async (string id, string requestId,
            string? projectId, DecisionRequest body, CancellationToken ct) =>
        {
            var store = Resolve(projectId);
            if (store is null || await store.GetAsync(id, ct) is null) return Results.NotFound();
            try
            {
                await store.ReviewWorkerDependencyAsync(id, requestId, body.Status, ct);
                return Results.Ok();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }
}
