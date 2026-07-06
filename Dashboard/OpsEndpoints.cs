using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// P6 Stage 3 — Ops pages. Adds the three endpoints the new
/// Blazor ops pages need that the per-stage endpoints files
/// didn't expose:
///   - GET /api/recovery/policies (static policy list)
///   - GET /api/cost/headroom (proxies Headroom /stats if reachable)
/// </summary>
public static class OpsEndpoints
{
    public sealed record RecoveryPolicyDto(
        string Id, string Name, string When, string Action, string Why);

    public sealed record HeadroomStatsDto(
        bool Enabled,
        bool ProxyReachable,
        string? ProxyBaseUrl,
        long? CallsLast1h,
        long? SavedInputTokens,
        double? SavedPct,
        string? Error);

    public static void MapOpsEndpoints(
        WebApplication app,
        CostTracker? costTracker,
        HeadroomOptions headroom,
        ILogger logger)
    {
        app.MapGet("/api/recovery/policies", () =>
        {
            var policies = new[]
            {
                new RecoveryPolicyDto(
                    "replay",
                    "Replay from last checkpoint",
                    "DispatchCheckpoint != null AND checkpointAt within the replay window",
                    "Replay",
                    "Restart the dispatch loop from the recorded checkpoint and continue forward."),
                new RecoveryPolicyDto(
                    "reclaim",
                    "Re-claim into Pending",
                    "RecoveryAttempts < 3 AND no checkpoint recorded",
                    "Reclaim",
                    "Reset the issue to Pending so a fresh agent can claim it."),
                new RecoveryPolicyDto(
                    "left-alone",
                    "Leave in place",
                    "Checkpoint is recent (within 60s) — another worker may still own it",
                    "LeftAlone",
                    "Do nothing; the in-flight worker is expected to finish."),
                new RecoveryPolicyDto(
                    "manual",
                    "Flag for operator decision",
                    "RecoveryAttempts >= 3",
                    "Failed",
                    "Mark the issue as failed and surface it on the Recovery tab for review."),
            };
            return Results.Json(policies);
        });

        app.MapGet("/api/cost/headroom", async (CancellationToken ct) =>
        {
            if (!headroom.Enabled || string.IsNullOrWhiteSpace(headroom.ProxyBaseUrl))
            {
                return Results.Json(new HeadroomStatsDto(
                    Enabled: false,
                    ProxyReachable: false,
                    ProxyBaseUrl: headroom.ProxyBaseUrl,
                    CallsLast1h: null,
                    SavedInputTokens: null,
                    SavedPct: null,
                    Error: null));
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var statsUrl = headroom.ProxyBaseUrl.TrimEnd('/') + "/stats";
                var resp = await http.GetAsync(statsUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return Results.Json(new HeadroomStatsDto(
                        Enabled: true,
                        ProxyReachable: false,
                        ProxyBaseUrl: headroom.ProxyBaseUrl,
                        CallsLast1h: null,
                        SavedInputTokens: null,
                        SavedPct: null,
                        Error: $"proxy returned {(int)resp.StatusCode}"));
                }

                var raw = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                long? calls = root.TryGetProperty("calls_last_1h", out var c) && c.TryGetInt64(out var cv) ? cv : null;
                long? saved = root.TryGetProperty("saved_input_tokens", out var s) && s.TryGetInt64(out var sv) ? sv : null;
                double? pct = root.TryGetProperty("saved_pct", out var p) && p.TryGetDouble(out var pv) ? pv : null;

                return Results.Json(new HeadroomStatsDto(
                    Enabled: true,
                    ProxyReachable: true,
                    ProxyBaseUrl: headroom.ProxyBaseUrl,
                    CallsLast1h: calls,
                    SavedInputTokens: saved,
                    SavedPct: pct,
                    Error: null));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Headroom proxy unreachable");
                return Results.Json(new HeadroomStatsDto(
                    Enabled: true,
                    ProxyReachable: false,
                    ProxyBaseUrl: headroom.ProxyBaseUrl,
                    CallsLast1h: null,
                    SavedInputTokens: null,
                    SavedPct: null,
                    Error: ex.Message));
            }
        });
    }
}