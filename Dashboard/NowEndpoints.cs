using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;
using Forge.Dashboard.Now;
using Forge.Orchestrator.Sprint;
using Forge.Projects;

namespace Forge.Dashboard;

/// <summary>
/// GET /api/now — the operator landing feed: attention items, live
/// activity, plain-language waiting reasons, and pipeline pulse.
/// Composed from existing stores; no new writes.
/// Multi-project: ?projectId= reads that project's stores; absent =
/// primary (ids are per-project sequences — reading the wrong store
/// silently returns the primary project's same-numbered rows).
/// </summary>
public static class NowEndpoints
{
    public static void MapNowEndpoints(
        WebApplication app,
        IIssueStore issues,
        ISpecStore specs,
        ISprintStore sprints,
        MemoryStore? memory,
        AgentRunStore? runs = null,
        ProjectContextFactory? projectContexts = null)
    {
        app.MapGet("/api/now", async (string? projectId, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;

            // UNIFIED by default (operator rule 2026-07-30): /api/now
            // with no lens is the cross-project admin view — every
            // item carries its projectId. ?projectId= scopes to one
            // project (same response shape, one entry per list).
            var targets = new List<(string? ProjectId, IIssueStore Issues, ISprintStore Sprints, AgentRunStore? Runs)>();
            if (projectId is not null && projectContexts is not null)
            {
                var pctx = projectContexts.Find(projectId);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                targets.Add((projectId, pctx.Issues, pctx.Sprints,
                    runs is null ? null : new AgentRunStore(((Core.IssueStore)pctx.Issues).Db)));
            }
            else if (projectContexts is not null)
            {
                foreach (var p in projectContexts.KnownProjects)
                {
                    var pctx = projectContexts.Find(p.Id);
                    if (pctx is not null)
                    {
                        targets.Add((p.Id, pctx.Issues, pctx.Sprints,
                            runs is null ? null : new AgentRunStore(((Core.IssueStore)pctx.Issues).Db)));
                    }
                }
            }
            if (targets.Count == 0)
            {
                // Legacy single-store fallback (tests / no factory).
                targets.Add((null, issues, sprints, runs));
            }

            var gates = memory is null
                ? (IReadOnlyDictionary<string, bool>)new Dictionary<string, bool>()
                : await new StageGates(memory).SnapshotAsync(ct);

            var attention = new List<(int Rank, object View)>();
            var live = new List<object>();
            var waiting = new List<object>();
            var sprintChips = new List<object>();
            DateTime? lastActivity = null;

            foreach (var (pid, issueStore, sprintStore, runStore) in targets)
            {
                var all = (await issueStore.ListAsync(new IssueFilter(), ct)).ToList();
                var byId = all.ToDictionary(i => i.Id);

                var active = await sprintStore.GetActiveAsync(ct);
                var sprintMembers = active is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(await sprintStore.GetIssueIdsAsync(active.Id, ct), StringComparer.Ordinal);

                foreach (var a in NowFeed.BuildAttention(all, gates, now))
                {
                    var rank = a.Severity == "fail" ? 0 : a.Severity == "warn" ? 1 : 2;
                    attention.Add((rank, (object)new
                    {
                        severity = a.Severity, kind = a.Kind, title = a.Title,
                        detail = a.Detail, issueId = a.IssueId, projectId = pid,
                        // Cross-project link: the detail page must read
                        // the item's OWNING project, not the lens.
                        link = a.IssueId is not null
                            ? $"/tasks/{a.IssueId}" + (pid is null ? "" : $"?project={pid}")
                            : "/sprints",
                    }));
                }

                // Watchdog findings (alert-only v1, 2026-07-31):
                // structural stalls the scanner detected — surfaced
                // here with their severity-ranked position.
                try
                {
                    var findings = await new Forge.Core.WatchdogFindingStore((Forge.Core.IssueStore)issueStore).ListOpenAsync(ct);
                    foreach (var f in findings)
                    {
                        attention.Add((f.Severity == "fail" ? 0 : 1, (object)new
                        {
                            severity = f.Severity, kind = $"watchdog:{f.Kind}",
                            title = $"watchdog: {f.Kind} ({f.TargetId})",
                            detail = f.Detail, issueId = f.TargetId.StartsWith("task-", StringComparison.Ordinal) ? f.TargetId : null,
                            projectId = pid,
                            link = f.TargetId.StartsWith("task-", StringComparison.Ordinal)
                                ? $"/tasks/{f.TargetId}" + (pid is null ? "" : $"?project={pid}")
                                : "/sprints",
                        }));
                    }
                }
                catch { /* finding store is additive; never breaks the feed */ }

                // Live-run phase labels (v25): the run registry's phase
                // column feeds the live cards so a verifying/reviewing
                // run doesn't read as idle. Per-project run store —
                // task ids collide across projects.
                IReadOnlyDictionary<string, string?>? phases = null;
                if (runStore is not null)
                {
                    try
                    {
                        phases = (await runStore.ListActiveAsync(ct))
                            .Where(r => r.TaskId is not null)
                            .GroupBy(r => r.TaskId!, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.First().Phase, StringComparer.Ordinal);
                    }
                    catch { phases = null; /* phase labels are additive */ }
                }
                foreach (var l in NowFeed.BuildLive(all, now, phases))
                {
                    live.Add(new
                    {
                        issueId = l.IssueId, projectId = pid, title = l.Title,
                        stage = l.Stage, elapsedMs = l.ElapsedMs, phase = l.Phase,
                    });
                }

                // Waiting reasons for open Pending tasks (newest first,
                // capped per project). The last transition detail
                // disambiguates "retrying after X" from plain "queued".
                foreach (var i in all
                    .Where(i => i.Status == IssueStatus.Pending
                        && i.Type != AgentTaskTypes.PrWatch
                        && !AgentTaskTypes.IsContainer(i.Type))
                    .OrderByDescending(i => i.UpdatedAt)
                    .Take(20))
                {
                    var events = await issueStore.ListEventsAsync(i.Id, limit: 5, ct);
                    var lastDetail = events.FirstOrDefault(e =>
                        e.Kind == "status_change" && e.Detail?.Contains("->Pending", StringComparison.Ordinal) == true)?.Detail;
                    var w = NowFeed.Reason(
                        i, sprintMembers.Contains(i.Id),
                        SprintAssembler.ResolveGroupKey(i, byId) != SprintAssembler.AdHocGroupName,
                        active?.Name, lastDetail, now);
                    waiting.Add(new
                    {
                        issueId = w.IssueId, projectId = pid, title = w.Title,
                        reason = w.Reason, waitingMs = w.WaitingMs,
                    });
                }

                if (active is not null)
                {
                    var sprintDone = sprintMembers.Count(id =>
                        byId.TryGetValue(id, out var m)
                        && m.Status is IssueStatus.Completed or IssueStatus.Closed);
                    sprintChips.Add(new
                    {
                        projectId = pid, id = active.Id, name = active.Name,
                        done = sprintDone, total = sprintMembers.Count,
                    });
                }
                if (all.Count > 0)
                {
                    var projectLast = all.Max(i => i.UpdatedAt);
                    lastActivity = lastActivity is null || projectLast > lastActivity ? projectLast : lastActivity;
                }
            }

            return Results.Json(new
            {
                generatedAt = now,
                attention = attention.OrderBy(a => a.Rank).Select(a => a.View),
                live,
                waiting,
                pulse = new
                {
                    gates = gates.ToDictionary(kv => kv.Key, kv => kv.Value ? "hold" : "open"),
                    sprints = sprintChips,
                    lastActivityAt = lastActivity,
                },
            });
        });
    }
}
