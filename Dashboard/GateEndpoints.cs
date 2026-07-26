using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Operator stage gates: inspect and hold/release the optional
/// review gates at the pipeline's major automatic transitions
/// (design, groom, sprint, merge). A held gate pauses that stage
/// until released — the observation deck for bringing a feature
/// through the pipeline step by step.
/// </summary>
public static class GateEndpoints
{
    public static void MapGateEndpoints(
        WebApplication app,
        StageGates gates,
        ILogger logger)
    {
        app.MapGet("/api/gates", async (CancellationToken ct) =>
        {
            var snap = await gates.SnapshotAsync(ct);
            return Results.Json(snap.ToDictionary(kv => kv.Key, kv => kv.Value ? "hold" : "open"));
        });

        app.MapPost("/api/gates/{stage}/hold", async (string stage, CancellationToken ct) =>
        {
            if (!StageGates.IsKnown(stage)) return Results.BadRequest(new { error = "unknown_stage", stage });
            await gates.HoldAsync(stage, ct);
            logger.LogInformation("Stage gate {Stage} HELD by operator", stage);
            return Results.Json(new { stage, state = "hold" });
        });

        app.MapPost("/api/gates/{stage}/release", async (string stage, CancellationToken ct) =>
        {
            if (!StageGates.IsKnown(stage)) return Results.BadRequest(new { error = "unknown_stage", stage });
            await gates.ReleaseAsync(stage, ct);
            logger.LogInformation("Stage gate {Stage} released by operator", stage);
            return Results.Json(new { stage, state = "open" });
        });
    }
}
