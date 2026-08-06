using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

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
        ILogger logger,
        AgentRunStore? runs = null,
        Projects.ProjectContextFactory? projectContexts = null)
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

        // Persisted per-role token rollup (v31) — unlike the
        // in-memory tracker this survives restarts and is
        // attributable per project. This is the "are we abusing the
        // context window?" answer: watch inputTokens vs
        // cacheReadTokens and peakContextTokens per role.
        app.MapGet("/api/cost/by-role", async (int? days, string? projectId, CancellationToken ct) =>
        {
            var store = runs;
            if (projectId is not null && projectContexts is not null)
            {
                var pctx = projectContexts.Find(projectId);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = pctx.Issues is IssueStore concrete ? new AgentRunStore(concrete.Db) : null;
            }
            if (store is null)
                return Results.Json(new { error = "run store not available in this mode" }, statusCode: 503);
            var rows = await store.SummarizeTokensByRoleAsync(days ?? 7, ct);
            return (IResult)Results.Json(new { days = days ?? 7, roles = rows });
        });

        app.MapPost("/api/cost/reset", () =>
        {
            tracker.Reset();
            logger.LogInformation("CostTracker reset by operator.");
            return Results.Json(new { reset = true });
        });
    }
}