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

        // PUT — write a DB override for this checkpoint.
        app.MapPut("/api/gates/{checkpoint}", async (string checkpoint, HttpContext ctx, CancellationToken ct) =>
        {
            if (memory is null)
                return Results.Problem("Memory store not available", statusCode: 503);

            SetGateOverrideRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SetGateOverrideRequest>(
                    ctx.Request.Body, DashboardJson.Options, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }

            if (body is null || body.GateNames is null)
                return Results.BadRequest(new { error = "gateNames required" });

            if (body.GateNames.Count == 0)
                return Results.BadRequest(new { error = "gateNames must not be empty" });

            // Validate every name against the catalog; warn about unknown names
            // but don't reject (the pipeline already skips unknown names).
            var unknown = body.GateNames
                .Where(n => !RunGatePipeline.GateCatalog.ContainsKey(n))
                .ToList();
            if (unknown.Count > 0)
            {
                logger.LogWarning(
                    "Gate override for {Checkpoint} contains unknown gate names: {Unknown}",
                    checkpoint, string.Join(", ", unknown));
            }

            var json = JsonSerializer.Serialize(body.GateNames, DashboardJson.Options);
            await memory.RememberAsync($"gates/run/{checkpoint}", json, ct: ct);

            logger.LogInformation(
                "Gate override saved for {Checkpoint}: {Names}",
                checkpoint, string.Join(", ", body.GateNames));

            return Results.Json(new { checkpoint, gateNames = body.GateNames, source = "db_override" }, DashboardJson.Options);
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
            return Results.NoContent();
        });
    }

    private sealed record SetGateOverrideRequest(List<string> GateNames);
}
