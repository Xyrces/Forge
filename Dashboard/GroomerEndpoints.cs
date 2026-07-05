using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// P3.5: HTTP endpoints for the Groomer timeline. The dashboard
/// shows every scheduled or manual run, when it ran, what
/// happened, and (when successful) how many stories / tasks the
/// Groomer produced.
/// </summary>
public static class GroomerEndpoints
{
    public static void MapGroomerEndpoints(
        WebApplication app,
        IssueGroomerRunStore runs,
        ILogger logger)
    {
        app.MapGet("/api/groomer/runs", async (string? specId, int? limit, CancellationToken ct) =>
        {
            try
            {
                var list = await runs.ListAsync(specId, limit ?? 100, ct);
                return Results.Json(list.Select(ToView).ToArray());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "/api/groomer/runs failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }

    private static object ToView(IssueGroomerRun r) => new
    {
        id = r.Id,
        ts = r.Ts,
        specId = r.SpecId,
        trigger = r.Trigger.ToString().ToLowerInvariant(),
        status = r.Status.ToString().ToLowerInvariant(),
        storiesProduced = r.StoriesProduced,
        tasksProduced = r.TasksProduced,
        error = r.Error,
        durationMs = r.DurationMs,
    };
}