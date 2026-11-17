using Forge.Core;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

public static class TaskRegroomEndpoints
{
    public static void MapTaskRegroomEndpoints(WebApplication app, IIssueStore primary,
        ProjectContextFactory? projects, ILogger logger)
    {
        IssueStore? Resolve(string? projectId) => projectId is null ? primary as IssueStore
            : projects is not null ? projects.Find(projectId)?.Issues as IssueStore
            : primary is IssueStore store && store.ProjectId == projectId ? store : null;
        app.MapGet("/api/tasks/{id}/regroom", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = Resolve(projectId);
            if (store is null || await store.GetAsync(id, ct) is null) return Results.NotFound(new { error = "project or task not found" });
            return Results.Json(await store.PreviewRegroomAsync(id, ct));
        });
        app.MapPost("/api/tasks/{id}/regroom", async (string id, string? projectId, TaskRegroomRequest body, CancellationToken ct) =>
        {
            var store = Resolve(projectId);
            if (store is null || await store.GetAsync(id, ct) is null) return Results.NotFound(new { error = "project or task not found" });
            try
            {
                await store.RequestRegroomAsync(id, body, "operator", ct);
                logger.LogInformation("Operator requested re-grooming for {Project}/{Task}", store.ProjectId, id);
                return Results.Json(new { taskId = id, status = "Pending", groomed = false });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }
}
