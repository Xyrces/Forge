using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Meshy;
using Forge.Orchestrator;

namespace Forge.Dashboard;

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
            var list = await ArtistStoreRouting.ListAsync(specs, id, false, ct);
            return Results.Json(list.Select(ToView).ToArray());
        });

        app.MapGet("/api/art-output/{id}/file", async (string id, string? specId, CancellationToken ct) =>
        {
            ArtOutput? art;
            string? root = meshy.ArtOutputRoot;
            if (specId is null)
            {
                // Existing links identify records in the original primary store.
                art = await artOutputs.GetAsync(id, ct);
                if (art is null)
                {
                    try
                    {
                        var historical = specs is ProjectRoutingSpecStore historyRouter
                            ? await historyRouter.ReadHistoricalArtAcrossProjectsAsync(id, ct)
                            : specs is SpecStore single
                                ? await new OwnedArtifactHistoryStore(single.Database).GetArtAsync(id, ct) : null;
                        art = historical?.Artifact;
                        root = historical?.ArtOutputRoot;
                    }
                    catch (ArtifactHistoryAmbiguousException)
                    {
                        return Results.Conflict(new { error = "ambiguous_history" });
                    }
                }
            }
            else
            {
                var owner = specs is ProjectRoutingSpecStore routing
                    ? routing.ResolveOwner(specId) as SpecStore : specs as SpecStore;
                if (owner is null || await owner.GetAsync(specId, ct) is null)
                    return Results.NotFound(new { error = "art_output_not_found" });
                art = await new ArtOutputStore(owner.Database).GetAsync(id, ct);
                root = artistFactory?.ArtOutputRoot(owner) ?? meshy.ArtOutputRoot;
                if (art is null)
                {
                    try
                    {
                        var historical = await new OwnedArtifactHistoryStore(owner.Database).GetArtAsync(id, ct);
                        art = historical?.Artifact;
                        root = historical?.ArtOutputRoot;
                    }
                    catch (ArtifactHistoryAmbiguousException)
                    {
                        return Results.Conflict(new { error = "ambiguous_history" });
                    }
                }
                if (art?.SpecId != specId) return Results.NotFound(new { error = "art_output_not_found" });
            }
            if (art is null) return Results.NotFound(new { error = "art_output_not_found" });
            string? full;
            try { full = root is null ? null : ArtifactFilePath.Resolve(root, art.SpecId, art.Body); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(ex, "Art output file unavailable for {Id}", id);
                full = null;
            }
            if (full is null) return Results.NotFound(new { error = "file_not_found" });
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
        fileUrl = $"/api/art-output/{a.Id}/file?specId={Uri.EscapeDataString(a.SpecId)}",
        references = a.ReferencesJson is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(a.ReferencesJson),
    };
}
