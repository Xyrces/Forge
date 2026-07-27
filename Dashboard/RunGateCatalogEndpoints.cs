using System.Text.Json;
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
/// resolution source. Also provides PUT (override) and DELETE
/// (reset) for operator control over the ordered gate list.
/// Used by the run-quality-gates dashboard page.
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
        // GET — resolved gate catalog with source annotation.
        app.MapGet("/api/gates/{checkpoint}", async (string checkpoint, CancellationToken ct) =>
        {
            if (memory is null)
                return Results.StatusCode(503);

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

        // PUT — persist an ordered gate list override via memory key.
        app.MapPut("/api/gates/{checkpoint}", async (string checkpoint, PutGateOverrideRequest request, CancellationToken ct) =>
        {
            if (memory is null)
                return Results.StatusCode(503);

            var json = JsonSerializer.Serialize(request.Gates, DashboardJson.Options);
            await memory.RememberAsync($"gates/run/{checkpoint}", json, ct: ct);
            logger.LogInformation("Run-gate override set for checkpoint {Checkpoint}: [{Gates}]", checkpoint, string.Join(", ", request.Gates));

            // Return the resolved state after the mutation.
            var pipeline = new RunGatePipeline(options, memory, _ => null, logger);
            var (names, source) = await pipeline.ResolveWithSourceAsync(checkpoint, ct);
            return ToResolvedResponse(checkpoint, names, source);
        });

        // DELETE — remove the memory-key override, restoring config -> built-in default.
        app.MapDelete("/api/gates/{checkpoint}", async (string checkpoint, CancellationToken ct) =>
        {
            if (memory is null)
                return Results.StatusCode(503);

            await memory.ForgetAsync($"gates/run/{checkpoint}", ct);
            logger.LogInformation("Run-gate override removed for checkpoint {Checkpoint}, reverting to config/default", checkpoint);

            // Return the resolved state (will now come from config or defaults).
            var pipeline = new RunGatePipeline(options, memory, _ => null, logger);
            var (names, source) = await pipeline.ResolveWithSourceAsync(checkpoint, ct);

            if (names.Count == 0 && source == "unknown")
            {
                return Results.NotFound(new { error = "unknown_checkpoint", checkpoint });
            }

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
