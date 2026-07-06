using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// P6 Stage 9 — Engineering Dispatch workflow endpoints.
///   GET   /api/tasks/in-progress           -> full task row + last 10 events
///   POST  /api/tasks/{id}/retry-message   -> inject a string into AgentMessageBus
///   POST  /api/tasks/{id}/recover         -> per-task recovery run
/// </summary>
public static class TaskEndpoints
{
    public sealed record TaskEventDto(string Kind, DateTime At, string? Detail);

    public sealed record InProgressTaskDto(
        string Id,
        string Type,
        string Title,
        string Status,
        int Priority,
        string? Assignee,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? ClosedAt,
        string? DispatchCheckpoint,
        int RecoveryAttempts,
        string? PrUrl,
        string? Branch,
        string? WorktreePath,
        IReadOnlyList<TaskEventDto> Events);

    public static void MapTaskEndpoints(
        WebApplication app,
        IIssueStore issues,
        AgentMessageBus? messageBus,
        Orchestrator.StartupRecovery? recovery,
        ILogger logger)
    {
        app.MapGet("/api/tasks/in-progress", async (int? limit, CancellationToken ct) =>
        {
            try
            {
                var inFlight = await issues.ListInProgressForRecoveryAsync(ct);
                var rows = new List<InProgressTaskDto>(inFlight.Count);
                foreach (var t in inFlight.Take(limit ?? 100))
                {
                    var events = await issues.ListEventsAsync(t.Id, limit: 10, ct);
                    rows.Add(new InProgressTaskDto(
                        Id: t.Id,
                        Type: t.Type,
                        Title: t.Title,
                        Status: t.Status.ToString(),
                        Priority: t.Priority,
                        Assignee: t.Assignee,
                        CreatedAt: t.CreatedAt,
                        UpdatedAt: t.UpdatedAt,
                        ClosedAt: t.ClosedAt,
                        DispatchCheckpoint: t.DispatchCheckpoint?.ToString(),
                        RecoveryAttempts: t.RecoveryAttempts,
                        PrUrl: ExtractMeta(t.MetadataJson, "prUrl"),
                        Branch: ExtractMeta(t.MetadataJson, "branch"),
                        WorktreePath: ExtractMeta(t.MetadataJson, "worktreePath"),
                        Events: events.Select(e => new TaskEventDto(e.Kind, e.Timestamp, e.Detail)).ToArray()));
                }
                return Results.Json(rows);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/tasks/in-progress failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/retry-message", async (string id, HttpContext ctx) =>
        {
            if (messageBus is null) return Results.Problem(detail: "AgentMessageBus not configured", statusCode: 503);
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (!doc.RootElement.TryGetProperty("text", out var textEl))
                    return Results.BadRequest(new { error = "text required" });
                var text = textEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    return Results.BadRequest(new { error = "text cannot be empty" });

                messageBus.Enqueue(id, text);
                return Results.Json(new { accepted = true, taskId = id });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/retry-message failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/recover", async (string id, CancellationToken ct) =>
        {
            if (recovery is null) return Results.Problem(detail: "StartupRecovery not configured", statusCode: 503);
            try
            {
                var reportId = await recovery.RunAsync(specId: null, ct: ct);
                return Results.Json(new { taskId = id, reportId });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/recover failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }

    private static string? ExtractMeta(string? metadataJson, string key)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
        }
        catch { }
        return null;
    }
}