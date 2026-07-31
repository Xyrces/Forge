using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// P6 Stage 5 — project-wide reads for the Designs + Art pages.
/// The per-spec reads already exist in DesignerEndpoints.cs and
/// ArtistEndpoints.cs. These endpoints aggregate across all specs
/// for the dashboard grid.
/// </summary>
public static class DesignArtEndpoints
{
    public sealed record DesignView(
        string Id,
        string SpecId,
        string Kind,
        string Title,
        string BodyKind,
        string Status,
        string Author,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    public sealed record ArtView(
        string Id,
        string SpecId,
        string Kind,
        string Title,
        string BodyKind,
        string Status,
        string Author,
        string FileUrl,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    public sealed record MeshyTaskDto(
        string Id,
        string Mode,
        string Status,
        string? ArtOutputId,
        string? GlbUrl);

    public static void MapDesignArtEndpoints(
        WebApplication app,
        DesignArtifactStore designArtifacts,
        ArtOutputStore artOutputs,
        DesignerRunStore? designerRuns,
        ArtistRunStore? artistRuns,
        ILogger logger)
    {
        app.MapGet("/api/designs", async (string? projectId, string? status, CancellationToken ct) =>
        {
            try
            {
                DesignArtifactStatus? statusFilter = null;
                if (!string.IsNullOrEmpty(status) && Enum.TryParse<DesignArtifactStatus>(status, ignoreCase: true, out var s))
                    statusFilter = s;

                if (string.IsNullOrEmpty(projectId))
                {
                    return Results.Json(Array.Empty<DesignView>(), DashboardJson.Options);
                }

                var list = await designArtifacts.ListByProjectAsync(projectId, ct);
                var filtered = statusFilter is null
                    ? list
                    : list.Where(a => a.Status == statusFilter.Value).ToArray();
                return Results.Json(filtered.Select(ToDesignView).ToArray(), DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/designs failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/specs/{id}/design-runs/latest", async (string id, CancellationToken ct) =>
        {
            if (designerRuns is null) return Results.Json(new { }, DashboardJson.Options);
            try
            {
                var list = await designerRuns.ListAsync(id, limit: 1, ct);
                var run = list.FirstOrDefault();
                if (run is null) return Results.Json(new { }, DashboardJson.Options);

                JsonElement? hygiene = null;
                if (!string.IsNullOrEmpty(run.HygieneReportJson))
                {
                    hygiene = JsonSerializer.Deserialize<JsonElement>(run.HygieneReportJson);
                }
                return Results.Json(new
                {
                    id = run.Id,
                    ts = run.Ts,
                    status = run.Status.ToString(),
                    newSpecStatus = run.NewSpecStatus?.ToString(),
                    designArtifactIds = run.DesignArtifactIds,
                    hygiene,
                    error = run.Error,
                    durationMs = run.DurationMs,
                }, DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/specs/{Id}/design-runs/latest failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/art-output", async (string? projectId, string? status, CancellationToken ct) =>
        {
            try
            {
                ArtOutputStatus? statusFilter = null;
                if (!string.IsNullOrEmpty(status) && Enum.TryParse<ArtOutputStatus>(status, ignoreCase: true, out var s))
                    statusFilter = s;

                if (string.IsNullOrEmpty(projectId))
                {
                    return Results.Json(Array.Empty<ArtView>(), DashboardJson.Options);
                }

                var list = await artOutputs.ListByProjectAsync(projectId, statusFilter, ct);
                return Results.Json(list.Select(ToArtView).ToArray(), DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/art-output failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/artist/runs/{id:long}/meshy-tasks", async (long id, CancellationToken ct) =>
        {
            if (artistRuns is null) return Results.Json(Array.Empty<MeshyTaskDto>(), DashboardJson.Options);
            try
            {
                var list = await artistRuns.ListAsync(specId: null, limit: 200, ct);
                var run = list.FirstOrDefault(r => r.Id == id);
                if (run is null) return Results.Json(Array.Empty<MeshyTaskDto>(), DashboardJson.Options);
                var tasks = (run.MeshyTasks ?? new List<MeshyTaskRecord>())
                    .Select(t => new MeshyTaskDto(t.Id, t.Mode.ToString(), t.Status.ToString(), t.ArtOutputId, t.GlbUrl))
                    .ToArray();
                return Results.Json(tasks, DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/artist/runs/{Id}/meshy-tasks failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }

    private static DesignView ToDesignView(DesignArtifact a) => new(
        a.Id, a.SpecId, a.Kind.ToString(), a.Title, a.BodyKind,
        a.Status.ToString(), a.Author, a.CreatedAt, a.UpdatedAt);

    private static ArtView ToArtView(ArtOutput a) => new(
        a.Id, a.SpecId, a.Kind.ToString(), a.Title, a.BodyKind,
        a.Status.ToString(), a.Author,
        $"/api/art-output/{a.Id}/file", a.CreatedAt, a.UpdatedAt);
}