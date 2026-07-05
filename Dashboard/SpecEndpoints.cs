using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;

namespace Forge.Dashboard;

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
        ISpecExtractionReader extractor,
        ILogger logger,
        Forge.Core.IIntakeStore? intakeStore = null,
        GroomerAgentFactory? groomerFactory = null,
        IssueGroomerRunStore? groomerRuns = null)
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

        // Phase 2b: extracted-tables reads. The dashboard's Spec
        // side-panel renders diagrams from spec_diagram; the Graph
        // tab reads spec_touches; the Deps tab reads spec_dep.
        app.MapGet("/api/specs/{id}/diagrams", async (string id, CancellationToken ct) =>
        {
            var diagrams = await extractor.GetDiagramsAsync(id, ct);
            return Results.Json(diagrams.Select(d => new
            {
                specId = d.SpecId,
                ordinal = d.Ordinal,
                kind = d.Kind,
                source = d.Source,
                title = d.Title
            }).ToArray(), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}/touches", async (string id, CancellationToken ct) =>
        {
            var touches = await extractor.GetTouchesAsync(id, ct);
            return Results.Json(touches.Select(t => new
            {
                specId = t.SpecId,
                moduleId = t.ModuleId,
                source = t.Source,
                rationale = t.Rationale,
                createdAt = t.CreatedAt
            }).ToArray(), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}/deps", async (string id, CancellationToken ct) =>
        {
            var deps = await extractor.GetDepsAsync(id, ct);
            return Results.Json(deps.Select(d => new
            {
                fromSpecId = d.FromSpecId,
                toSpecId = d.ToSpecId,
                kind = d.Kind,
                rationale = d.Rationale,
                source = d.Source,
                createdAt = d.CreatedAt
            }).ToArray(), DashboardJson.Options);
        });

        // Phase 2b: lookup specs produced by a specific intake
        // session. Used by the intake tab side-panel to render the
        // master + children of an in-progress intake.
        app.MapGet("/api/intake/sessions/{sessionId}/specs", async (string sessionId, CancellationToken ct) =>
        {
            if (intakeStore is null) return Results.Json(Array.Empty<object>(), DashboardJson.Options);
            var session = await intakeStore.GetAsync(sessionId, ct);
            if (session is null) return Results.NotFound();
            var proposed = session.Messages
                .Where(m => m.ProposedEpicId is not null)
                .Select(m => m.ProposedEpicId!)
                .Distinct()
                .ToList();
            var allSpecs = new List<SpecRecord>();
            foreach (var pid in proposed)
            {
                var match = await extractor.ListByParentIssueIdAsync(pid, ct);
                allSpecs.AddRange(match);
            }
            return Results.Json(allSpecs.Select(ToSpecView).ToArray(), DashboardJson.Options);
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

        // Phase 3.5: operator-triggered grooming. Returns immediately;
        // the agent runs on a worker thread and emits dashboard events
        // (groomer.run.started / completed / failed) as it works.
        if (groomerFactory is not null)
        {
            app.MapPost("/api/specs/{id}/groom", async (string id, CancellationToken ct) =>
            {
                var spec = await specs.GetAsync(id, ct);
                if (spec is null)
                    return Results.NotFound(new { error = "spec_not_found" });
                // P2.a: the manual groom endpoint now accepts any of
                // the "ready to groom" statuses: Designed (Designer
                // approved), Approved (operator non-visual fast-path),
                // Groomed (operator re-decompose).
                if (spec.Status is not (SpecStatus.Designed
                    or SpecStatus.AssetReady
                    or SpecStatus.Approved
                    or SpecStatus.Groomed))
                {
                    return Results.BadRequest(new
                    {
                        error = "spec_not_groomable",
                        detail = $"spec status is {spec.Status}; expected Designed | AssetReady | Approved | Groomed"
                    });
                }

                // Fire-and-forget on a background task. The HTTP
                // request returns immediately so the UI can refresh
                // and watch the event stream. The manual run is
                // recorded in issue_groomer_run (P3.5) so the
                // dashboard's Groomer timeline can show it.
                var agent = groomerFactory.Create();
                var runs = groomerRuns;
                _ = Task.Run(async () =>
                {
                    var run = runs is not null
                        ? await runs.StartAsync(id, GroomerTriggerKind.Manual, CancellationToken.None)
                        : null;
                    var startedAt = DateTime.UtcNow;
                    try
                    {
                        await agent.GroomAsync(id);
                        if (runs is not null && run is not null)
                        {
                            await runs.FinishAsync(run.Id, GroomerRunStatus.Succeeded,
                                storiesProduced: 0, tasksProduced: 0, error: null,
                                duration: DateTime.UtcNow - startedAt,
                                ct: CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (runs is not null && run is not null)
                        {
                            await runs.FinishAsync(run.Id, GroomerRunStatus.Failed,
                                storiesProduced: 0, tasksProduced: 0,
                                error: $"{ex.GetType().Name}: {ex.Message}",
                                duration: DateTime.UtcNow - startedAt,
                                ct: CancellationToken.None);
                        }
                        logger.LogWarning(ex, "Background groom failed for spec {Id}", id);
                    }
                });
                return Results.Accepted($"/api/specs/{id}", new { status = "started" });
            });
        }
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
