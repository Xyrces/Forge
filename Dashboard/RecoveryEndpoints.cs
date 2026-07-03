using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Orchestrator;

namespace PortHorizon.Agents.Dashboard;

/// <summary>
/// P4 Stage A — Recovery endpoints. See
/// <c>docs/p4-restart-safety.md</c>.
///
/// <list type="bullet">
///   <item><c>GET /api/recovery/reports</c> — the most recent
///   audit rows (default 50). The operator can pin the count
///   with <c>?limit=N</c>.</item>
///   <item><c>GET /api/recovery/reports/{id}</c> — a single
///   audit row including the actions_json detail.</item>
///   <item><c>POST /api/recovery/run</c> — fires a fresh
///   StartupRecovery pass against the live DB. Returns the new
///   audit row's id + summary. Side-effects enabled (this is
///   the same code path as <c>--recover-and-start</c> after the
///   recovery completes).</item>
///   <item><c>POST /api/recovery/dry-run</c> — classifies every
///   candidate without side-effects. Returns a JSON array of
///   {issueId, action, reason}.</item>
/// </list>
/// </summary>
public static class RecoveryEndpoints
{
    public static void MapRecoveryEndpoints(
        WebApplication app,
        IIssueStore issues,
        RecoveryReportStore reports,
        StartupRecovery recovery,
        ILogger logger)
    {
        app.MapGet("/api/recovery/reports", async (int? limit, CancellationToken ct) =>
        {
            var list = await reports.ListAsync(limit ?? 50, ct);
            return Results.Json(list.Select(ToView).ToArray());
        });

        app.MapGet("/api/recovery/reports/{id:long}", async (long id, CancellationToken ct) =>
        {
            var r = await reports.GetAsync(id, ct);
            return r is null ? Results.NotFound(new { error = "report_not_found" }) : Results.Json(ToView(r));
        });

        app.MapPost("/api/recovery/run", async (CancellationToken ct) =>
        {
            try
            {
                var id = await recovery.RunAsync(ct: ct);
                var r = await reports.GetAsync(id, ct);
                return Results.Json(new { reportId = id, summary = r is null ? null : ToView(r) });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manual recovery run crashed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/recovery/dry-run", async (CancellationToken ct) =>
        {
            var candidates = await issues.ListInProgressForRecoveryAsync(ct);
            var decisions = candidates.Select(i =>
            {
                var d = recovery.Classify(i);
                return new
                {
                    issueId = i.Id,
                    checkpoint = i.DispatchCheckpoint?.ToDbValue(),
                    action = d.Action.ToString(),
                    reason = d.Reason,
                };
            }).ToArray();
            return Results.Json(new { scanned = candidates.Count, decisions });
        });
    }

    private static object ToView(RecoveryReportRecord r) => new
    {
        id = r.Id,
        ts = r.Ts,
        specId = r.SpecId,
        issuesScanned = r.IssuesScanned,
        issuesReplayed = r.IssuesReplayed,
        issuesFailed = r.IssuesFailed,
        durationMs = r.DurationMs,
        actions = r.ActionsJson,
    };
}