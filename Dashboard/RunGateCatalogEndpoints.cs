using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents.Gates;
using Forge.Configuration;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Read-only endpoint that returns the resolved ordered gate list
/// for a checkpoint, with each gate's name, kind, description, and
/// resolution source. Used by the run-quality-gates dashboard page.
/// </summary>
public static class RunGateCatalogEndpoints
{
    public static void MapRunGateCatalogEndpoints(
        WebApplication app,
        GateOptions options,
        MemoryStore? memory,
        ILogger logger)
    {
        app.MapGet("/api/gates/{checkpoint}", async (string checkpoint, CancellationToken ct) =>
        {
            // Construct a lightweight pipeline for resolution only
            // (no gate instances needed for the catalog).
            var pipeline = new RunGatePipeline(
                options,
                memory,
                _ => null,  // no gate instances needed for catalog lookup
                logger);

            var (names, source) = await pipeline.ResolveWithSourceAsync(checkpoint, ct);

            if (names.Count == 0 && source == "unknown")
            {
                return Results.NotFound(new { error = "unknown_checkpoint", checkpoint });
            }

            var gates = names.Select(name =>
            {
                string? kind = null;
                string? description = null;
                if (RunGatePipeline.GateCatalog.TryGetValue(name, out var entry))
                {
                    kind = entry.Kind.ToString();
                    description = entry.Description;
                }
                return new { name, kind, description, source };
            }).ToList();

            return Results.Json(new { checkpoint, source, gates }, DashboardJson.Options);
        });
    }
}
