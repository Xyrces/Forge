using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Orchestrator;

namespace PortHorizon.Agents.Dashboard;

/// <summary>
/// P2.a: HTTP endpoints for the Designer pipeline.
///
/// <list type="bullet">
///   <item><c>POST /api/specs/{id}/design</c> — manual run, returns
///   202 + the run's id; the Designer runs in the background.</item>
///   <item><c>GET /api/designer/runs?specId=...</c> — the timeline.</item>
///   <item><c>GET /api/specs/{id}/design-artifacts</c> — the spec's
///   design_artifact rows for the Design tab.</item>
/// </list>
/// </summary>
public static class DesignerEndpoints
{
    public static void MapDesignerEndpoints(
        WebApplication app,
        ISpecStore specs,
        DesignerAgentFactory? designerFactory,
        DesignerRunStore runs,
        DesignArtifactStore artifacts,
        ILogger logger)
    {
        if (designerFactory is not null)
        {
            app.MapPost("/api/specs/{id}/design", async (string id, CancellationToken ct) =>
            {
                var spec = await specs.GetAsync(id, ct);
                if (spec is null)
                    return Results.NotFound(new { error = "spec_not_found" });
                if (spec.Status is not (SpecStatus.ReadyForDesign
                    or SpecStatus.NeedsRevision
                    or SpecStatus.Draft
                    or SpecStatus.Approved))
                {
                    return Results.BadRequest(new
                    {
                        error = "spec_not_designable",
                        detail = $"spec status is {spec.Status}; Designer only processes ReadyForDesign / NeedsRevision / Draft / Approved (Approved re-design).",
                    });
                }

                // Fire-and-forget on a background task. The HTTP
                // request returns immediately so the UI can refresh
                // and watch the event stream. The manual run is
                // recorded in designer_run with trigger=manual.
                var agent = designerFactory.Create();
                _ = Task.Run(async () =>
                {
                    try { await agent.DesignSpecAsync(id, DesignerTriggerKind.Manual, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Background design failed for spec {Id}", id);
                    }
                });
                return Results.Accepted($"/api/specs/{id}", new { status = "started" });
            });
        }

        app.MapGet("/api/designer/runs", async (string? specId, int? limit, CancellationToken ct) =>
        {
            var list = await runs.ListAsync(specId, limit ?? 100, ct);
            return Results.Json(list.Select(ToView).ToArray());
        });

        app.MapGet("/api/specs/{id}/design-artifacts", async (string id, CancellationToken ct) =>
        {
            var list = await artifacts.ListBySpecAsync(id);
            return Results.Json(list.Select(ToView).ToArray());
        });
    }

    private static object ToView(DesignerRun r) => new
    {
        id = r.Id,
        ts = r.Ts,
        specId = r.SpecId,
        trigger = r.Trigger.ToString().ToLowerInvariant(),
        status = r.Status.ToString().ToLowerInvariant(),
        newSpecStatus = r.NewSpecStatus?.ToString().ToLowerInvariant(),
        designArtifactIds = r.DesignArtifactIds,
        hygieneReport = r.HygieneReportJson,
        error = r.Error,
        durationMs = r.DurationMs,
    };

    private static object ToView(DesignArtifact a) => new
    {
        id = a.Id,
        specId = a.SpecId,
        kind = a.Kind.ToString().ToLowerInvariant(),
        title = a.Title,
        body = a.Body,
        bodyKind = a.BodyKind,
        status = a.Status.ToString().ToLowerInvariant(),
        author = a.Author,
        createdAt = a.CreatedAt,
        updatedAt = a.UpdatedAt,
        references = a.ReferencesJson is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(a.ReferencesJson),
    };
}