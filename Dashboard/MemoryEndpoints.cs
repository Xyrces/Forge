using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

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
}