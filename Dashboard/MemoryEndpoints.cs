using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// HTTP endpoints for the memory store (the bd remember/prime
/// analog from docs/embedded-issues.md Phase 3). Read-only by
/// default for the dashboard; POST/DELETE require no auth in v1
/// since the operator already has shell on the host.
/// </summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(
        WebApplication app,
        MemoryStore memory,
        ILogger logger)
    {
        app.MapGet("/api/memory", async (string? prefix, CancellationToken ct) =>
        {
            try
            {
                var list = await memory.RecallAsync(prefix, ct);
                return Results.Json(list.Select(ToMemoryView).ToArray(), DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/memory failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/memory", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<RememberRequest>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (body is null || string.IsNullOrWhiteSpace(body.Key) || body.Body is null)
                return Results.BadRequest(new { error = "key and body required" });
            if (body.TtlDays is { } d && (d < 1 || d > 3650))
                return Results.BadRequest(new { error = "ttlDays must be between 1 and 3650" });

            try
            {
                var rec = await memory.RememberAsync(body.Key, body.Body, body.TtlDays, ctx.RequestAborted);
                logger.LogInformation("Memory remember: key={Key} ttl={Ttl}", body.Key, body.TtlDays);
                return Results.Json(ToMemoryView(rec), DashboardJson.Options, statusCode: 201);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/memory/{key}", async (string key, CancellationToken ct) =>
        {
            var removed = await memory.ForgetAsync(key, ct);
            if (!removed) return Results.NotFound();
            logger.LogInformation("Memory forget: key={Key}", key);
            return Results.NoContent();
        });
    }

    private static object ToMemoryView(MemoryRecord r) => new
    {
        id = r.Id,
        key = r.Key,
        body = r.Body,
        ttlDays = r.TtlDays,
        createdAt = r.CreatedAt,
        expiresAt = r.ExpiresAt,
    };

    public sealed record RememberRequest(string Key, string Body, int? TtlDays);

    // ---------- P5.6: extraction audit log ----------

    /// <summary>
    /// P5.6: per-task read of the memory extraction audit log
    /// (the v13 <c>memory_extraction</c> table). Returns the
    /// runs in chronological order so the dashboard can render
    /// "this commit produced 2 memories; here are the keys".
    /// Combined with the existing <c>GET /api/memory?prefix=extraction/{id}/</c>
    /// the operator can pivot from audit log to the actual
    /// stored values.
    /// </summary>
    public static void MapExtractionEndpoints(
        WebApplication app,
        MemoryExtractionStore extractions,
        ILogger logger)
    {
        app.MapGet("/api/memory/extractions", async (int? limit, CancellationToken ct) =>
        {
            try
            {
                var list = await extractions.ListAsync(limit ?? 100, ct);
                return Results.Json(list.Select(ToExtractionView).ToArray(), DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/memory/extractions failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/memory/extractions/{taskId}", async (string taskId, CancellationToken ct) =>
        {
            try
            {
                var list = await extractions.ListForTaskAsync(taskId, ct);
                return Results.Json(list.Select(ToExtractionView).ToArray(), DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/memory/extractions/{Id} failed", taskId);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }

    private static object ToExtractionView(MemoryExtractionRecord r) => new
    {
        id = r.Id,
        timestamp = r.Timestamp,
        taskId = r.TaskId,
        sourceChars = r.SourceChars,
        extractedCount = r.ExtractedCount,
        persistedKeys = r.PersistedKeys,
        error = r.Error,
    };
}