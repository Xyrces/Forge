using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using Forge.Core;

namespace Forge.Dashboard;

public static class HealthEndpoint
{
    public static void MapHealthEndpoint(this WebApplication app, DefaultHealthSnapshotFactory factory)
    {
        app.MapGet("/api/forgesystem/health", () => Results.Json(factory.Snapshot(), DashboardJson.Options));
        app.MapGet("/api/health/uptime", () => Results.Json(factory.Uptime(), DashboardJson.Options));
    }
}

public sealed class DefaultHealthSnapshotFactory
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public HealthSnapshot Snapshot()
    {
        var dashboardListening = true;
        var lastRecoveryId = "rpt-latest";
        var lastRecoveryFailed = false;
        var lastDeploymentId = "deploy-latest";
        var lastDeploymentFailed = false;
        var projectCount = 1;
        var status =
            !dashboardListening ? "down" :
            (lastRecoveryFailed || lastDeploymentFailed) ? "degraded" :
            "ok";
        return new HealthSnapshot(
            UptimeSeconds: (long)_sw.Elapsed.TotalSeconds,
            DashboardListening: dashboardListening,
            ProjectCount: projectCount,
            LastRecoveryReportId: lastRecoveryId,
            LastDeploymentId: lastDeploymentId,
            Status: status);
    }

    public UptimeSnapshot Uptime() => new(
        UptimeMs: _sw.ElapsedMilliseconds,
        UtcTimestamp: DateTime.UtcNow);
}

public sealed record HealthSnapshot(
    long UptimeSeconds,
    bool DashboardListening,
    int ProjectCount,
    string? LastRecoveryReportId,
    string? LastDeploymentId,
    string Status);

public sealed record UptimeSnapshot(long UptimeMs, DateTime UtcTimestamp);
