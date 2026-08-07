using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// P6 Stage 8 — Proposed Next Sprint view backend.
/// GET  /api/sprints/propose-next            -> ProposalResult
/// POST /api/sprints/propose-next/commit     -> { auditId, sprintId }
/// GET  /api/sprints/{id}/scoring-audit      -> [SprintProposalAuditRecord]
/// </summary>
public static class SprintProposeEndpoints
{
    public static void MapSprintProposeEndpoints(
        WebApplication app,
        SprintProposeService proposer,
        SprintProposalAuditStore audit,
        ILogger logger,
        Projects.ProjectContextFactory? projectContexts = null,
        IIssueStore? primaryIssues = null)
    {
        // Per-project resolution: the injected proposer/audit are the
        // PRIMARY project's. A ?projectId= request gets a service over
        // that project's own stores — scored tasks, the created sprint,
        // and the audit row all live in the OWNING project's schema
        // (sprint/task ids are per-project sequences, so committing a
        // proposal against the primary store would attach another
        // project's tasks to the primary project's same-numbered rows).
        SprintProposeService ResolveService(
            string? projectId,
            out SprintProposalAuditStore auditStore,
            out IIssueStore? issueStore,
            out IResult? error)
        {
            auditStore = audit;
            issueStore = primaryIssues;
            error = null;
            if (projectId is null || projectContexts is null) return proposer;
            var ctx = projectContexts.Find(projectId);
            if (ctx is null)
            {
                error = Results.NotFound(new { error = "project not found", projectId });
                return proposer;
            }
            auditStore = new SprintProposalAuditStore(((Core.IssueStore)ctx.Issues).Db);
            issueStore = ctx.Issues;
            return new SprintProposeService(ctx.Issues, ctx.Sprints, new Agents.DeterministicScorer(), auditStore);
        }

        app.MapGet("/api/sprints/propose-next", async (string? theme, string? goal, int? count, string? projectId, CancellationToken ct) =>
        {
            try
            {
                var svc = ResolveService(projectId, out _, out _, out var err);
                if (err is not null) return err;
                var result = await svc.ProposeAsync(theme, goal, count ?? SprintProposeService.DefaultCandidateCount, ct);
                return Results.Json(new
                {
                    auditId = result.AuditId,
                    theme = result.Theme,
                    goal = result.Goal,
                    scoredAt = result.ScoredAt,
                    candidates = result.Candidates.Select(c => new
                    {
                        taskId = c.TaskId,
                        title = c.Title,
                        score = c.Score,
                        breakdown = c.Breakdown,
                    }).ToArray(),
                    selectedTaskIds = result.SelectedTaskIds,
                    weights = result.Weights,
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/sprints/propose-next failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/sprints/propose-next/commit", async (HttpContext ctx) =>
        {
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                var root = doc.RootElement;
                if (!root.TryGetProperty("auditId", out var auditEl) || !root.TryGetProperty("taskIds", out var tasksEl))
                    return Results.BadRequest(new { error = "auditId and taskIds required" });

                var auditId = auditEl.GetInt64();
                var taskIds = tasksEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                var theme = root.TryGetProperty("theme", out var thEl) ? thEl.GetString() : null;
                var goal = root.TryGetProperty("goal", out var gEl) ? gEl.GetString() : null;
                var by = root.TryGetProperty("committedBy", out var byEl) ? byEl.GetString() ?? "operator" : "operator";
                var projectId = root.TryGetProperty("projectId", out var pidEl) ? pidEl.GetString() : null;

                var svc = ResolveService(projectId, out _, out var issueStore, out var err);
                if (err is not null) return err;

                // The audit row and the task ids live in the RESOLVED
                // project's store. A propose/commit lens mismatch must
                // not silently commit the other store's same-numbered
                // audit row (audit ids are per-store identity
                // sequences) or attach foreign task ids to the sprint.
                if (issueStore is not null)
                {
                    var unknown = new List<string>();
                    foreach (var tid in taskIds)
                    {
                        if (await issueStore.GetAsync(tid, ctx.RequestAborted) is null)
                            unknown.Add(tid);
                    }
                    if (unknown.Count > 0)
                        return Results.BadRequest(new { error = "task ids not found in this project's store", taskIds = unknown });
                }
                try
                {
                    var sprintId = await svc.CommitAsync(auditId, taskIds, theme, goal, by, ctx.RequestAborted);
                    return Results.Json(new { auditId, sprintId });
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.NotFound(new { error = ex.Message, projectId });
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/sprints/propose-next/commit failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/sprints/scoring-audit", async (int? limit, string? projectId, CancellationToken ct) =>
        {
            try
            {
                ResolveService(projectId, out var auditStore, out _, out var err);
                if (err is not null) return err;
                var list = await auditStore.ListAsync(limit ?? 50, ct);
                var rows = list.Select(r => new
                {
                    id = r.Id,
                    timestamp = r.Timestamp,
                    theme = r.Theme,
                    goal = r.Goal,
                    weightsJson = r.WeightsJson,
                    candidatesJson = r.CandidatesJson,
                    selectedTaskIdsJson = r.SelectedTaskIdsJson,
                    committedSprintId = r.CommittedSprintId,
                    committedBy = r.CommittedBy,
                }).ToArray();
                return Results.Json(rows);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/sprints/scoring-audit failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }
}