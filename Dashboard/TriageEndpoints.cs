using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Failure-triage ledger (phase 1 — observability only):
///   GET /api/triage/ledger?projectId=              -> summary strip + signature-grouped view
///   GET /api/triage/ledger/{signature}?projectId=  -> the group's individual rows
/// Per-project lens only (no cross-project aggregation — /now stays
/// as-is). code-bug-suspect is DERIVED here (same signature across ≥3
/// distinct tasks), never stored.
/// </summary>
public static class TriageEndpoints
{
    private const int BugSuspectTaskThreshold = 3;
    private const int EscalationBudget = 5; // phase-3 budget; placeholder in phase 1

    public sealed record TriageSummaryDto(
        int OpenFailures,
        int DistinctSignatures7d,
        int Escalations7d,
        int EscalationBudget,
        int AutoCleared7d,
        IReadOnlyList<int> DailyOpenFailures7d,
        IReadOnlyList<int> DailyDistinctSignatures7d,
        IReadOnlyList<int> DailyAutoCleared7d);

    public sealed record TriageSignatureGroupDto(
        string Signature,
        string Classification,
        int Count,
        int DistinctTasks,
        DateTime LastSeenAt,
        string? LastTaskId,
        string? DominantOutcome,
        bool BugSuspect);

    public sealed record TriageHealthDto(int PlanGateRejections7d, int NoDiffBounces7d, int VerificationTimeouts7d);

    public sealed record TriageLedgerResponse(
        TriageSummaryDto Summary,
        IReadOnlyList<TriageSignatureGroupDto> Groups,
        TriageHealthDto Health);

    public sealed record TriageEntryDto(
        long Id, string TaskId, string? TaskTitle, DateTime FailedAt,
        string? ErrorExcerpt, string? Action, string? Actor, DateTime? ActedAt, string? Outcome);

    public static void MapTriageEndpoints(
        WebApplication app,
        IIssueStore issues,
        ILogger logger,
        Projects.ProjectContextFactory? projectContexts = null)
    {
        app.MapGet("/api/triage/ledger", async (string? projectId, CancellationToken ct) =>
        {
            var (triage, _) = Resolve(projectId);
            if (triage is null) return Results.NotFound(new { error = "project not found", projectId });
            try
            {
                var rows = await triage.ListAsync(ct: ct);
                var now = DateTime.UtcNow;
                var since7d = now.AddDays(-7);
                var rows7d = rows.Where(r => r.FailedAt >= since7d).ToList();

                var summary = new TriageSummaryDto(
                    OpenFailures: rows.Count(r => r.Action is null || r.Outcome == FailureTriageOutcomes.Pending),
                    DistinctSignatures7d: rows7d.Select(r => r.Signature).Distinct(StringComparer.Ordinal).Count(),
                    Escalations7d: 0, // phase 3 wires per-task model escalation
                    EscalationBudget: EscalationBudget,
                    AutoCleared7d: rows.Count(r => r.Action == FailureTriageActions.AgedSweep
                        && r.ActedAt >= since7d),
                    DailyOpenFailures7d: DailyCounts(rows7d, r => true, now),
                    DailyDistinctSignatures7d: DailyDistinct(rows7d, now),
                    DailyAutoCleared7d: DailyCounts(
                        rows.Where(r => r.Action == FailureTriageActions.AgedSweep && r.ActedAt >= since7d),
                        r => true, now, r => r.ActedAt!.Value));

                var groups = rows
                    .GroupBy(r => r.Signature, StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var list = g.ToList();
                        var dominant = list
                            .Where(r => r.Outcome is not null)
                            .GroupBy(r => r.Outcome!)
                            .OrderByDescending(x => x.Count())
                            .Select(x => x.Key)
                            .FirstOrDefault();
                        return new TriageSignatureGroupDto(
                            Signature: g.Key,
                            Classification: list[0].Classification,
                            Count: list.Count,
                            DistinctTasks: list.Select(r => r.TaskId).Distinct(StringComparer.Ordinal).Count(),
                            LastSeenAt: list.Max(r => r.FailedAt),
                            LastTaskId: list.OrderByDescending(r => r.FailedAt).First().TaskId,
                            DominantOutcome: dominant,
                            BugSuspect: list.Select(r => r.TaskId).Distinct(StringComparer.Ordinal).Count() >= BugSuspectTaskThreshold);
                    })
                    .OrderByDescending(g => g.LastSeenAt)
                    .ToList();

                var health = new TriageHealthDto(
                    PlanGateRejections7d: rows7d.Count(r => r.Classification == "gate-loop"),
                    NoDiffBounces7d: rows7d.Count(r => r.Classification == "no-progress"),
                    VerificationTimeouts7d: rows7d.Count(r =>
                        r.Classification == "verification" && r.Signature == "verification-timeout"));

                return Results.Json(new TriageLedgerResponse(summary, groups, health));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/triage/ledger failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/triage/ledger/{signature}", async (string signature, string? projectId, CancellationToken ct) =>
        {
            var (triage, store) = Resolve(projectId);
            if (triage is null || store is null) return Results.NotFound(new { error = "project not found", projectId });
            try
            {
                var rows = (await triage.ListAsync(ct: ct))
                    .Where(r => string.Equals(r.Signature, signature, StringComparison.Ordinal))
                    .ToList();
                var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var taskId in rows.Select(r => r.TaskId).Distinct(StringComparer.Ordinal))
                {
                    var task = await store.GetAsync(taskId, ct);
                    titles[taskId] = task?.Title;
                }
                var entries = rows.Select(r => new TriageEntryDto(
                    r.Id, r.TaskId, titles.GetValueOrDefault(r.TaskId), r.FailedAt,
                    r.ErrorExcerpt, r.Action, r.Actor, r.ActedAt, r.Outcome)).ToList();
                return Results.Json(entries);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/triage/ledger/{Signature} failed", signature);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        (FailureTriageStore?, IIssueStore?) Resolve(string? projectId)
        {
            if (projectId is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(projectId);
                if (ctx is null) return (null, null);
                return (ctx.Triage, ctx.Issues);
            }
            return issues is IssueStore concrete
                ? (new FailureTriageStore(concrete), issues)
                : (null, null);
        }
    }

    /// <summary>Failures-per-day buckets, oldest first (7 days ending today).</summary>
    private static IReadOnlyList<int> DailyCounts(
        IEnumerable<FailureTriageEntry> rows, Func<FailureTriageEntry, bool> pred, DateTime now,
        Func<FailureTriageEntry, DateTime>? when = null)
    {
        var buckets = new int[7];
        var today = now.Date;
        foreach (var r in rows)
        {
            if (!pred(r)) continue;
            var day = (when?.Invoke(r) ?? r.FailedAt).Date;
            var offset = (today - day).Days;
            if (offset is >= 0 and < 7) buckets[6 - offset]++;
        }
        return buckets;
    }

    private static IReadOnlyList<int> DailyDistinct(IReadOnlyList<FailureTriageEntry> rows7d, DateTime now)
    {
        var today = now.Date;
        var buckets = new int[7];
        for (var i = 0; i < 7; i++)
        {
            var day = today.AddDays(-(6 - i));
            buckets[i] = rows7d.Where(r => r.FailedAt.Date == day)
                .Select(r => r.Signature).Distinct(StringComparer.Ordinal).Count();
        }
        return buckets;
    }
}
