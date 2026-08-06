using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator.Slots;
using Forge.Projects;

namespace Forge.Dashboard;

/// <summary>
/// GET /api/queue?projectId= — the engineering ready queue for one
/// project, in claim order (blocker boost → priority → FIFO, the same
/// ordering <c>IssueStore.ReadyAsync</c> feeds the dispatch loop),
/// with a per-item wait reason: ready | slot-full | model-cooling |
/// awaiting-groom. Sprint members only (the orchestrator never
/// dispatches non-members); blocked tasks are absent by construction
/// — ReadyAsync excludes anything with an open blocks edge.
/// Per-project only (operator rule: everything but /now is a lens);
/// absent projectId = primary.
/// </summary>
public static class QueueEndpoints
{
    public static void MapQueueEndpoints(
        WebApplication app,
        IIssueStore primaryIssues,
        ISprintStore primarySprints,
        ProjectContextFactory? projectContexts,
        SlotTable? slots,
        LlmConfig? llmConfig,
        RoleModelOverrides? modelOverrides,
        ModelRateLimitTracker? rateLimits,
        string? primaryProjectId = null)
    {
        app.MapGet("/api/queue", async (string? projectId, CancellationToken ct) =>
        {
            IIssueStore issues = primaryIssues;
            ISprintStore sprints = primarySprints;
            var pid = projectId ?? primaryProjectId;
            if (projectContexts is not null)
            {
                pid ??= projectContexts.KnownProjects.FirstOrDefault()?.Id;
                if (pid is null) return Results.NotFound(new { error = "no project registered" });
                var pctx = projectContexts.Find(pid);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId = pid });
                issues = pctx.Issues;
                sprints = pctx.Sprints;
            }

            var active = await sprints.GetActiveAsync(ct);
            var memberIds = active is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(await sprints.GetIssueIdsAsync(active.Id, ct), StringComparer.Ordinal);

            var ready = await issues.ReadyAsync(0, (string?)null, ct);
            var meters = slots?.Snapshot() ?? (IReadOnlyList<SlotTable.SlotMeter>)Array.Empty<SlotTable.SlotMeter>();
            var registry = new RoleAgentRegistry();
            var boosted = await issues.OpenBlockingAsync(ready.Select(t => t.Id).ToArray(), ct);

            // Blocked members are NOT queue entries (ReadyAsync
            // excludes them) but the operator reconciles this panel
            // against the board's Queued column — show them in a
            // separate section with their open blockers so the math
            // adds up (operator 2026-07-31: "7 queued, queue empty?").
            var blockedBy = memberIds.Count == 0
                ? new Dictionary<string, IReadOnlyList<string>>()
                : new Dictionary<string, IReadOnlyList<string>>(
                    await issues.OpenBlockersAsync(memberIds.ToArray(), ct), StringComparer.Ordinal);
            var blockedMembers = new List<object>();
            if (blockedBy.Count > 0)
            {
                var pendingMembers = await issues.ListAsync(
                    new IssueFilter { Status = IssueStatus.Pending }, ct);
                foreach (var t in pendingMembers)
                {
                    if (!blockedBy.ContainsKey(t.Id)) continue;
                    if (t.Type == AgentTaskTypes.PrWatch || AgentTaskTypes.IsContainer(t.Type)) continue;
                    blockedMembers.Add(new
                    {
                        issueId = t.Id,
                        title = t.Title,
                        type = t.Type,
                        priority = t.Priority,
                        blockedBy = blockedBy[t.Id],
                    });
                }
            }

            var items = new List<object>();
            var position = 0;
            foreach (var t in ready)
            {
                // Mirror the dispatch filter: sprint members, no
                // containers, no watch rows.
                if (t.Type == AgentTaskTypes.PrWatch || AgentTaskTypes.IsContainer(t.Type)) continue;
                if (!memberIds.Contains(t.Id)) continue;
                position++;

                var role = registry.ForType(RoleAgentRegistry.FromTaskType(t.Type)).AgentName;
                string wait;
                string? detail = null;

                var ungroomed = t.ParentIssueId is null
                    && !string.Equals(t.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase);
                if (ungroomed)
                {
                    wait = "awaiting-groom";
                    detail = "member, pending technical grooming";
                }
                else
                {
                    DateTime? coolingUntil = null;
                    string? modelLabel = null;
                    if (llmConfig is not null)
                    {
                        try
                        {
                            var (provider, model, _) = llmConfig.ResolveEffective(
                                RoleAgentRegistry.FromTaskType(t.Type), modelOverrides, pid);
                            modelLabel = $"{provider.Name}/{model}";
                            coolingUntil = rateLimits?.CoolingDownUntil(provider.Name, model);
                        }
                        catch (InvalidOperationException) { /* resolution fallback: treat as not cooling */ }
                    }
                    if (coolingUntil is not null)
                    {
                        wait = "model-cooling";
                        detail = $"{modelLabel} cooling until {coolingUntil:HH:mm:ss} UTC";
                    }
                    else
                    {
                        var meter = meters.FirstOrDefault(m =>
                            string.Equals(m.ProjectId, pid, StringComparison.Ordinal)
                            && string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase));
                        if (meter.Max > 0 && meter.InFlight >= meter.Max)
                        {
                            wait = "slot-full";
                            detail = $"{role} pool {meter.InFlight}/{meter.Max} in flight";
                        }
                        else
                        {
                            wait = "ready";
                        }
                    }
                }

                items.Add(new
                {
                    position,
                    issueId = t.Id,
                    title = t.Title,
                    type = t.Type,
                    priority = t.Priority,
                    role,
                    boosted = boosted.Contains(t.Id),
                    wait,
                    detail,
                });
            }

            return Results.Ok(new
            {
                projectId = pid,
                sprintId = active?.Id,
                sprintName = active?.Name,
                items,
                blocked = blockedMembers,
            });
        });
    }
}
