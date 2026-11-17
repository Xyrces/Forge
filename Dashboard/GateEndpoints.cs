using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Core.Messaging;

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
        ILogger logger,
        IEventPublisher? publisher = null,
        IProjectStore? projects = null)
    {
        app.MapGet("/api/gates", async (CancellationToken ct) =>
        {
            var snap = await gates.SnapshotAsync(ct);
            return Results.Json(snap.ToDictionary(kv => kv.Key, kv => kv.Value ? "hold" : "open"));
        });

        app.MapPost("/api/gates/{stage}/hold", async (string stage, CancellationToken ct) =>
        {
            if (!StageGates.IsKnown(stage)) return Results.BadRequest(new { error = "unknown_stage", stage });
            stage = stage.ToLowerInvariant();
            await gates.HoldAsync(stage, ct);
            logger.LogInformation("Stage gate {Stage} HELD by operator", stage);
            return Results.Json(new { stage, state = "hold" });
        });

        app.MapPost("/api/gates/{stage}/release", async (string stage, CancellationToken ct) =>
        {
            if (!StageGates.IsKnown(stage)) return Results.BadRequest(new { error = "unknown_stage", stage });
            stage = stage.ToLowerInvariant();
            await gates.ReleaseAsync(stage, ct);
            // Releasing a persisted hold must wake the ordinary pipeline, not
            // wait for its 15-minute recovery backstop. Consumers re-read gates
            // and eligibility; this notification never dispatches work directly.
            // Deployment has its own 15-second durable reconciler, not a workflow sweep.
            if (stage != StageGates.Deploy && publisher is not null && projects is not null)
            {
                try
                {
                    using var notify = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    notify.CancelAfter(TimeSpan.FromSeconds(5));
                    var kind = stage.ToLowerInvariant() switch
                    {
                        StageGates.Design => SweepKind.Design,
                        StageGates.Groom => SweepKind.Groom,
                        StageGates.Sprint => SweepKind.Assemble,
                        StageGates.Merge => SweepKind.Watch,
                        _ => throw new InvalidOperationException("Unknown stage gate."),
                    };
                    var releasedAt = DateTimeOffset.UtcNow;
                    foreach (var project in await projects.ListAsync(notify.Token))
                    {
                        notify.Token.ThrowIfCancellationRequested();
                        try
                        {
                            await publisher.PublishAsync(new SweepTick
                            {
                                MessageId = SweepTick.IdFor(kind, project.Id, releasedAt),
                                ProjectId = project.Id,
                                Kind = kind,
                                TickAt = releasedAt,
                            }, notify.Token);
                        }
                        catch (Exception ex) when (!notify.IsCancellationRequested)
                        {
                            logger.LogError(ex, "Stage gate {Stage} released but project {ProjectId} wakeup failed", stage, project.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The release already committed. The periodic backstop
                    // remains the recovery path if notification is unavailable.
                    logger.LogError(ex, "Stage gate {Stage} released but pipeline wakeup failed", stage);
                }
            }
            logger.LogInformation("Stage gate {Stage} released by operator", stage);
            return Results.Json(new { stage, state = "open" });
        });
    }
}
