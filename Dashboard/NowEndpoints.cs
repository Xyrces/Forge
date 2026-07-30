using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;
using Forge.Dashboard.Now;
using Forge.Orchestrator.Sprint;

namespace Forge.Dashboard;

/// <summary>
/// GET /api/now — the operator landing feed: attention items, live
/// activity, plain-language waiting reasons, and pipeline pulse.
/// Composed from existing stores; no new writes.
/// </summary>
public static class NowEndpoints
{
    public static void MapNowEndpoints(
        WebApplication app,
        IIssueStore issues,
        ISpecStore specs,
        ISprintStore sprints,
        MemoryStore? memory,
        AgentRunStore? runs = null)
    {
        app.MapGet("/api/now", async (CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var all = (await issues.ListAsync(new IssueFilter(), ct)).ToList();
            var byId = all.ToDictionary(i => i.Id);

            var active = await sprints.GetActiveAsync(ct);
            var sprintMembers = active is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(await sprints.GetIssueIdsAsync(active.Id, ct), StringComparer.Ordinal);

            var gates = memory is null
                ? (IReadOnlyDictionary<string, bool>)new Dictionary<string, bool>()
                : await new StageGates(memory).SnapshotAsync(ct);

            var attention = NowFeed.BuildAttention(all, gates, now);
            // Live-run phase labels (v25): the run registry's phase
            // column feeds the live cards so a verifying/reviewing
            // run doesn't read as idle.
            IReadOnlyDictionary<string, string?>? phases = null;
            if (runs is not null)
            {
                try
                {
                    phases = (await runs.ListActiveAsync(ct))
                        .Where(r => r.TaskId is not null)
                        .GroupBy(r => r.TaskId!, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().Phase, StringComparer.Ordinal);
                }
                catch { phases = null; /* phase labels are additive */ }
            }
            var live = NowFeed.BuildLive(all, now, phases);

            // Waiting reasons for open Pending tasks (newest first,
            // capped). The last transition detail disambiguates
            // "retrying after X" from plain "queued".
            var waiting = new List<NowFeed.WaitingItem>();
            foreach (var i in all
                .Where(i => i.Status == IssueStatus.Pending
                    && i.Type != AgentTaskTypes.PrWatch
                    && !AgentTaskTypes.IsContainer(i.Type))
                .OrderByDescending(i => i.UpdatedAt)
                .Take(20))
            {
                var events = await issues.ListEventsAsync(i.Id, limit: 5, ct);
                var lastDetail = events.FirstOrDefault(e =>
                    e.Kind == "status_change" && e.Detail?.Contains("->Pending", StringComparison.Ordinal) == true)?.Detail;
                waiting.Add(NowFeed.Reason(
                    i, sprintMembers.Contains(i.Id),
                    SprintAssembler.ResolveGroupKey(i, byId) != SprintAssembler.AdHocGroupName,
                    active?.Name, lastDetail, now));
            }

            var sprintDone = 0;
            if (active is not null)
            {
                sprintDone = sprintMembers.Count(id =>
                    byId.TryGetValue(id, out var m)
                    && m.Status is IssueStatus.Completed or IssueStatus.Closed);
            }

            return Results.Json(new
            {
                generatedAt = now,
                attention = attention.Select(a => new
                {
                    severity = a.Severity, kind = a.Kind, title = a.Title,
                    detail = a.Detail, issueId = a.IssueId,
                    link = a.IssueId is not null ? $"/tasks/{a.IssueId}" : "/sprints",
                }),
                live = live.Select(l => new
                {
                    issueId = l.IssueId, title = l.Title, stage = l.Stage, elapsedMs = l.ElapsedMs,
                    phase = l.Phase,
                }),
                waiting = waiting.Select(w => new
                {
                    issueId = w.IssueId, title = w.Title, reason = w.Reason, waitingMs = w.WaitingMs,
                }),
                pulse = new
                {
                    gates = gates.ToDictionary(kv => kv.Key, kv => kv.Value ? "hold" : "open"),
                    sprint = active is null ? null : new
                    {
                        id = active.Id, name = active.Name, done = sprintDone, total = sprintMembers.Count,
                    },
                    lastActivityAt = all.Count == 0 ? (DateTime?)null : all.Max(i => i.UpdatedAt),
                },
            });
        });
    }
}
