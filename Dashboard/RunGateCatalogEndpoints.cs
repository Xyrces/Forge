using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents.Gates;
using Forge.Configuration;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Read-only and write endpoints for the run-quality-gate catalog.
/// GET returns the resolved ordered gate list with resolution source.
/// PUT writes a DB override via memory store. DELETE removes the
/// override, restoring config/built-in default resolution.
/// </summary>
public static class RunGateCatalogEndpoints
{
    private sealed record PutGateOverrideRequest(string[] Gates);

    public static void MapRunGateCatalogEndpoints(
        WebApplication app,
        GateOptions options,
        MemoryStore? memory,
        ILogger logger)
    {
        // GET — resolved gate list with source annotation.
        app.MapGet("/api/gates/{checkpoint}", async (string checkpoint, CancellationToken ct) =>
        {
            var pipeline = new RunGatePipeline(
                options,
                memory,
                _ => null,
                logger);

            var (names, source) = await pipeline.ResolveWithSourceAsync(checkpoint, ct);

            if (names.Count == 0 && source == "unknown")
            {
                return Results.NotFound(new { error = "unknown_checkpoint", checkpoint });
            }

            return ToResolvedResponse(checkpoint, names, source);
        });

        // PUT — write a DB override for this checkpoint.
        app.MapPut("/api/gates/{checkpoint}", async (string checkpoint, PutGateOverrideRequest request, CancellationToken ct) =>
        {
            if (memory is null)
                return Results.Problem("Memory store not available", statusCode: 503);

            if (request.Gates is null || request.Gates.Length == 0)
                return Results.BadRequest(new { error = "gates must not be empty" });

            // Warn about unknown names but don't reject (the pipeline already skips them).
            var unknown = request.Gates
                .Where(n => !RunGatePipeline.GateCatalog.ContainsKey(n))
                .ToList();
            if (unknown.Count > 0)
            {
                logger.LogWarning(
                    "Gate override for {Checkpoint} contains unknown gate names: {Unknown}",
                    checkpoint, string.Join(", ", unknown));
            }

            var json = JsonSerializer.Serialize(request.Gates, DashboardJson.Options);
            await memory.RememberAsync($"gates/run/{checkpoint}", json, ct: ct);

            logger.LogInformation(
                "Gate override saved for {Checkpoint}: {Names}",
                checkpoint, string.Join(", ", request.Gates));

            // Return the resolved state after the mutation.
            var pipeline = new RunGatePipeline(options, memory, _ => null, logger);
            var (names, source) = await pipeline.ResolveWithSourceAsync(checkpoint, ct);
            return ToResolvedResponse(checkpoint, names, source);
        });

        // DELETE — remove the DB override, resetting to config/built-in defaults.
        app.MapDelete("/api/gates/{checkpoint}", async (string checkpoint, CancellationToken ct) =>
        {
            if (memory is null)
                return Results.Problem("Memory store not available", statusCode: 503);

            var removed = await memory.ForgetAsync($"gates/run/{checkpoint}", ct);
            if (!removed)
                return Results.NotFound(new { error = "no_override_found", checkpoint });

            logger.LogInformation("Gate override removed for {Checkpoint} — reset to defaults", checkpoint);

            // Return the resolved state (will now come from config or defaults).
            var pipeline = new RunGatePipeline(options, memory, _ => null, logger);
            var (names, source) = await pipeline.ResolveWithSourceAsync(checkpoint, ct);

            if (names.Count == 0 && source == "unknown")
                return Results.NotFound(new { error = "unknown_checkpoint", checkpoint });

            return ToResolvedResponse(checkpoint, names, source);
        });
    }

    private static IResult ToResolvedResponse(string checkpoint, IReadOnlyList<string> names, string source)
    {
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
    }
}
