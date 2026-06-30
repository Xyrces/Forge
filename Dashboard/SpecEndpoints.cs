using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Dashboard;

/// <summary>
/// P1.5.a endpoints: spec CRUD + version history.
///
/// <para>
/// P1.5.a is read-only from the dashboard UI (no edit controls). The
/// endpoints are full CRUD so the Product agent (P1.5.b) and the
/// future groomer can write to specs; the dashboard tab just shows
/// what's there.
/// </para>
/// </summary>
public static class SpecEndpoints
{
    public static void MapSpecEndpoints(
        WebApplication app,
        ISpecStore specs,
        ILogger logger)
    {
        app.MapGet("/api/specs", async (string? project, string? status, CancellationToken ct) =>
        {
            SpecStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<SpecStatus>(status, ignoreCase: true, out var s))
                statusFilter = s;
            var list = await specs.ListAsync(project, statusFilter, ct);
            return Results.Json(list.Select(ToSpecView).ToArray(), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}", async (string id, CancellationToken ct) =>
        {
            var spec = await specs.GetAsync(id, ct);
            return spec is null
                ? Results.NotFound()
                : Results.Json(ToSpecView(spec), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}/versions", async (string id, CancellationToken ct) =>
        {
            var versions = await specs.ListVersionsAsync(id, ct);
            return Results.Json(versions.Select(ToVersionView).ToArray(), DashboardJson.Options);
        });

        app.MapPost("/api/specs", async (HttpContext ctx) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewSpec>(ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.ProjectId) || string.IsNullOrWhiteSpace(spec.Title))
                return Results.BadRequest(new { error = "projectId and title required" });
            try
            {
                var created = await specs.CreateAsync(spec, ctx.RequestAborted);
                return Results.Json(ToSpecView(created), DashboardJson.Options, statusCode: 201);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PATCH supports two operations: replace the body (creates a new
        // version) OR change the status. The request body's `op` field
        // picks which one: "update_body" or "set_status".
        app.MapPatch("/api/specs/{id}", async (string id, HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "expected object body" });
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl))
                return Results.BadRequest(new { error = "op required ('update_body' or 'set_status')" });
            var op = opEl.GetString();
            try
            {
                if (op == "set_status" && root.TryGetProperty("status", out var stEl)
                    && Enum.TryParse<SpecStatus>(stEl.GetString() ?? "", ignoreCase: true, out var newStatus))
                {
                    var updated = await specs.SetStatusAsync(id, newStatus, ctx.RequestAborted);
                    return Results.Json(ToSpecView(updated), DashboardJson.Options);
                }
                if (op == "update_body" && root.TryGetProperty("body", out var bodyEl))
                {
                    var bodyText = bodyEl.GetString() ?? "";
                    var author = root.TryGetProperty("author", out var aEl) ? aEl.GetString() : null;
                    var updated = await specs.UpdateBodyAsync(id, new UpdateSpecBody(bodyText, author), ctx.RequestAborted);
                    return Results.Json(ToSpecView(updated), DashboardJson.Options);
                }
                return Results.BadRequest(new { error = "unknown op (use 'update_body' or 'set_status')" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/specs/{id}", async (string id, CancellationToken ct) =>
        {
            await specs.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static object ToSpecView(SpecRecord s) => new
    {
        id = s.Id,
        projectId = s.ProjectId,
        title = s.Title,
        status = s.Status.ToString(),
        parentIssueId = s.ParentIssueId,
        parentSpecId = s.ParentSpecId,
        currentVersion = s.CurrentVersion,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
        body = s.Body,
        author = s.Author
    };

    private static object ToVersionView(SpecVersionRecord v) => new
    {
        specId = v.SpecId,
        version = v.Version,
        body = v.Body,
        author = v.Author,
        createdAt = v.CreatedAt
    };
}
