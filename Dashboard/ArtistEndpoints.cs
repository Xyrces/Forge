using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Orchestrator;

namespace PortHorizon.Agents.Dashboard;

/// <summary>
/// P2.b: HTTP endpoints for the Artist pipeline.
///
/// <list type="bullet">
///   <item><c>POST /api/specs/{id}/design-art</c> — manual run,
///   returns 202 + the run's id; the Artist runs in the
///   background.</item>
///   <item><c>GET /api/artist/runs?specId=...</c> — the timeline.</item>
///   <item><c>GET /api/specs/{id}/art-output</c> — the spec's
///   art_output rows for the Art tab.</item>
///   <item><c>GET /api/art-output/{id}/file</c> — streams the
///   underlying <c>.glb</c> (or other body_kind) for the
///   dashboard's Art tab. The path is resolved under
///   <c>.portHorizon/art-output/</c> with a sanitized
///   specId / fileName check to prevent path-traversal.</item>
/// </list>
/// </summary>
public static class ArtistEndpoints
{
    public static void MapArtistEndpoints(
        WebApplication app,
        ISpecStore specs,
        ArtistAgentFactory? artistFactory,
        ArtistRunStore runs,
        ArtOutputStore artOutputs,
        Meshy.MeshyClient meshy,
        ILogger logger)
    {
        if (artistFactory is not null)
        {
            app.MapPost("/api/specs/{id}/design-art", async (string id, CancellationToken ct) =>
            {
                var spec = await specs.GetAsync(id, ct);
                if (spec is null)
                    return Results.NotFound(new { error = "spec_not_found" });
                if (spec.Status is not (SpecStatus.Designed
                    or SpecStatus.AssetReady
                    or SpecStatus.NeedsRevision))
                {
                    return Results.BadRequest(new
                    {
                        error = "spec_not_artable",
                        detail = $"spec status is {spec.Status}; Artist only processes Designed / AssetReady (re-run) / NeedsRevision.",
                    });
                }

                // Fire-and-forget on a background task. The HTTP
                // request returns immediately so the UI can refresh
                // and watch the event stream. The manual run is
                // recorded in artist_run with trigger=manual.
                var agent = artistFactory.Create();
                _ = Task.Run(async () =>
                {
                    try { await agent.ArtSpecAsync(id, ArtistTriggerKind.Manual, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Background art failed for spec {Id}", id);
                    }
                });
                return Results.Accepted($"/api/specs/{id}", new { status = "started" });
            });
        }

        app.MapGet("/api/artist/runs", async (string? specId, int? limit, CancellationToken ct) =>
        {
            var list = await runs.ListAsync(specId, limit ?? 100, ct);
            return Results.Json(list.Select(ToView).ToArray());
        });

        app.MapGet("/api/specs/{id}/art-output", async (string id, CancellationToken ct) =>
        {
            var list = await artOutputs.ListBySpecAsync(id);
            return Results.Json(list.Select(ToView).ToArray());
        });

        app.MapGet("/api/art-output/{id}/file", async (string id, CancellationToken ct) =>
        {
            var art = await artOutputs.GetAsync(id, ct);
            if (art is null) return Results.NotFound(new { error = "art_output_not_found" });
            // The body is a relative path under
            // .portHorizon/art-output/{specId}/{id}.{ext}. Resolve
            // and verify it's still under the configured root
            // (path-traversal guard).
            var root = Path.GetFullPath(meshy.ArtOutputRoot);
            var full = Path.GetFullPath(Path.Combine(root, art.Body));
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "invalid_path" });
            }
            if (!File.Exists(full))
            {
                return Results.NotFound(new { error = "file_not_found", path = art.Body });
            }
            var contentType = art.BodyKind switch
            {
                "glb" => "model/gltf-binary",
                "fbx" => "application/octet-stream",
                "obj" => "text/plain",
                "usdz" => "model/vnd.usdz+zip",
                "png" => "image/png",
                "mp4" => "video/mp4",
                _ => "application/octet-stream",
            };
            return Results.File(full, contentType);
        });
    }

    private static object ToView(ArtistRun r) => new
    {
        id = r.Id,
        ts = r.Ts,
        specId = r.SpecId,
        trigger = r.Trigger.ToString().ToLowerInvariant(),
        status = r.Status.ToString().ToLowerInvariant(),
        newSpecStatus = r.NewSpecStatus?.ToString().ToLowerInvariant(),
        artOutputIds = r.ArtOutputIds,
        meshyTasks = r.MeshyTasks,
        error = r.Error,
        durationMs = r.DurationMs,
    };

    private static object ToView(ArtOutput a) => new
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
        fileUrl = $"/api/art-output/{a.Id}/file",
        references = a.ReferencesJson is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(a.ReferencesJson),
    };
}
