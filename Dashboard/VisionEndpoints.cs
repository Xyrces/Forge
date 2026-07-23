using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

/// <summary>
/// P0.5: HTTP endpoints for the vision store. The dashboard's
/// Vision tab fetches <c>GET /api/vision</c> on load and after the
/// "Refresh" button; the JSON response carries the loaded content
/// + metadata.
/// </summary>
public static class VisionEndpoints
{
    public static void MapVisionEndpoints(
        WebApplication app,
        VisionStore vision,
        ILogger logger,
        Core.MemoryStore? memory = null)
    {
        app.MapGet("/api/vision", () =>
        {
            var snap = vision.Get();
            return Results.Json(new
            {
                exists = snap.Exists,
                path = snap.Path,
                content = snap.Content,
                lastModified = snap.LastModifiedUtc,
            });
        });

        app.MapPost("/api/vision/refresh", () =>
        {
            var snap = vision.Reload();
            logger.LogInformation("Vision refreshed: exists={Exists} path={Path}", snap.Exists, snap.Path);
            return Results.Json(new
            {
                exists = snap.Exists,
                path = snap.Path,
                content = snap.Content,
                lastModified = snap.LastModifiedUtc,
            });
        });

        // Dashboard editor save path. Writes the file (creating it
        // if missing) and refreshes the vision/master memory key so
        // subsequent agent runs see the new vision immediately.
        app.MapPut("/api/vision", async (VisionUpdate update, CancellationToken ct) =>
        {
            var snap = vision.Write(update.Content ?? "");
            logger.LogInformation("Vision saved: {Path} ({Len} chars)", snap.Path, snap.Content.Length);
            if (memory is not null && snap.Exists)
                await memory.RememberAsync("vision/master", snap.Content, ttlDays: null, ct);
            return Results.Json(new
            {
                exists = snap.Exists,
                path = snap.Path,
                content = snap.Content,
                lastModified = snap.LastModifiedUtc,
            });
        });
    }

    public sealed record VisionUpdate(string? Content);
}