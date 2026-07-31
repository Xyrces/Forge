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
        ILogger logger)
    {
        app.MapGet("/api/sprints/propose-next", async (string? theme, string? goal, int? count, CancellationToken ct) =>
        {
            try
            {
                var result = await proposer.ProposeAsync(theme, goal, count ?? SprintProposeService.DefaultCandidateCount, ct);
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

                var sprintId = await proposer.CommitAsync(auditId, taskIds, theme, goal, by, ctx.RequestAborted);
                return Results.Json(new { auditId, sprintId });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/sprints/propose-next/commit failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapGet("/api/sprints/scoring-audit", async (int? limit, CancellationToken ct) =>
        {
            try
            {
                var list = await audit.ListAsync(limit ?? 50, ct);
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