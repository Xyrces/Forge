using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Dashboard;

/// <summary>
/// P4 Headroom observability. Reports per-call token usage
/// recorded by the orchestrator's
/// <see cref="CostTracker"/>. The dashboard polls this every
/// 30s (paired with a separate <c>/proxy-stats</c> poll when
/// Headroom is enabled).
///
/// <list type="bullet">
///   <item><c>GET /api/cost/stats</c> — totals + the most recent
///   ~200 calls (input/output tokens + role).</item>
///   <item><c>POST /api/cost/reset</c> — clear counters (useful
///   when you start a new run).</item>
/// </list>
/// </summary>
public static class CostEndpoints
{
    public static void MapCostEndpoints(
        WebApplication app,
        CostTracker tracker,
        ILogger logger)
    {
        app.MapGet("/api/cost/stats", () =>
        {
            var snap = tracker.Snapshot();
            return Results.Json(new
            {
                callCount = snap.CallCount,
                totalInputTokens = snap.TotalInputTokens,
                totalOutputTokens = snap.TotalOutputTokens,
                recent = snap.Recent.Select(r => new
                {
                    at = r.At,
                    inputTokens = r.InputTokens,
                    outputTokens = r.OutputTokens,
                    role = r.Role,
                }).ToArray(),
            });
        });

        app.MapPost("/api/cost/reset", () =>
        {
            tracker.Reset();
            logger.LogInformation("CostTracker reset by operator.");
            return Results.Json(new { reset = true });
        });
    }
}