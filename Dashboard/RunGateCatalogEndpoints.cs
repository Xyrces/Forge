using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents.Gates;
using Forge.Configuration;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Endpoints for inspecting and overriding the run-quality gate
/// catalog per checkpoint. GET returns the resolved gate list
/// (DB override -> config -> built-in defaults) with annotation;
/// PUT writes an override to the memory store for the checkpoint;
/// DELETE removes it.
/// </summary>
public static class RunGateCatalogEndpoints
{
    public static void MapRunGateCatalogEndpoints(
        WebApplication app,
        GateOptions options,
        MemoryStore? memory,
        ILogger logger)
    {
        // GET /api/gates/{checkpoint} — resolved catalog with source + kind + unknownNames
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

            var unknownNames = names.Where(n => !RunGatePipeline.GateCatalog.ContainsKey(n)).ToList();

            return Results.Json(new { checkpoint, source, gates, unknownNames }, DashboardJson.Options);
        });

        // PUT /api/gates/{checkpoint} — write gate override to memory
        app.MapPut("/api/gates/{checkpoint}", async (string checkpoint, PutGateOverrideRequest body, CancellationToken ct) =>
        {
            if (body.Gates is null || body.Gates.Length == 0)
            {
                return Results.BadRequest(new { error = "gates array must not be empty" });
            }

            if (memory is null)
            {
                return Results.Problem(detail: "Memory store not available", statusCode: 503);
            }

            var json = JsonSerializer.Serialize(body.Gates);
            await memory.RememberAsync($"gates/run/{checkpoint}", json, ct: ct);
            logger.LogInformation("Gate override written for checkpoint {Checkpoint}: {Gates}", checkpoint, json);

            return Results.Json(new { checkpoint, gates = body.Gates });
        });

        // DELETE /api/gates/{checkpoint} — remove gate override from memory
        app.MapDelete("/api/gates/{checkpoint}", async (string checkpoint, CancellationToken ct) =>
        {
            if (memory is null)
            {
                return Results.Problem(detail: "Memory store not available", statusCode: 503);
            }

            var removed = await memory.ForgetAsync($"gates/run/{checkpoint}", ct);
            if (!removed)
            {
                return Results.NotFound(new { error = "no_override_found", checkpoint });
            }

            logger.LogInformation("Gate override removed for checkpoint {Checkpoint}", checkpoint);
            return Results.NoContent();
        });
    }

    /// <summary>Request DTO for PUT /api/gates/{checkpoint}.</summary>
    public sealed record PutGateOverrideRequest(string[] Gates);
}
