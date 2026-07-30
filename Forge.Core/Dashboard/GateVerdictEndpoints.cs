using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Read-only endpoint that returns the last N run-quality gate
/// verdicts (approved/revised/rejected) with feedback text,
/// sourced from persisted RunGateState.Verdicts data via
/// <see cref="GateVerdictReader"/>.
///
/// No new write path — reads existing task metadata only.
/// </summary>
public static class GateVerdictEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static void MapGateVerdictEndpoints(
        WebApplication app,
        IIssueStore issues,
        ILogger logger)
    {
        app.MapGet("/api/gates/verdicts", async (int? limit, CancellationToken ct) =>
        {
            var cap = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var reader = new GateVerdictReader(issues);
            var results = await reader.ListRecentAsync(cap, ct);

            logger.LogDebug("Gate verdict endpoint: limit={Limit}, returned={Count}", cap, results.Count);

            return Results.Json(results, DashboardJson.Options);
        });
    }
}
