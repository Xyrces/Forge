using Forge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

/// <summary>Explicit, preview-bound correction of post-deployment acceptance represented as executable work.</summary>
public static class OperatorAcceptanceRepairEndpoints
{
    public static void MapOperatorAcceptanceRepairEndpoints(WebApplication app,
        Func<string?, IssueStore?> resolve, IssueGroomerRunStore? groomerRuns, ILogger logger)
    {
        app.MapGet("/api/specs/{id}/operator-checks/repair", async (string id, string? project, CancellationToken ct) =>
        {
            var issues = resolve(project);
            if (issues is null || (project is not null && project != issues.ProjectId))
                return Results.NotFound(new { error = "Project not found." });
            if (groomerRuns?.UsesDatabase(issues.Db) != true)
                return Results.Json(new { eligible = false, reason = "Planning evidence is not available in this project store." }, DashboardJson.Options);
            try
            {
                var availability = await new OperatorAcceptanceRepair(issues).PreviewAsync(id, ct);
                if (!availability.Eligible || availability.Preview is not { } preview)
                    return Results.Json(new { eligible = false, reason = availability.Reason }, DashboardJson.Options);
                return Results.Json(new
                {
                    eligible = true,
                    preview = new
                    {
                        preview.ProjectId, preview.SpecId, preview.SpecVersion, preview.GroomRunId, preview.PreviewHash,
                        preview.Authority, preview.OutcomeId, preview.Objective,
                        sourceRows = preview.SourceRows.Select(Row), targetRows = preview.TargetRows.Select(Row),
                        preview.TransferredCriteria,
                        criteria = preview.TargetBindings.Values.First().Criteria
                    }
                }, DashboardJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to preview operator acceptance repair for {SpecId} in {ProjectId}", id, issues.ProjectId);
                return Results.Json(new { error = "The planning correction could not be checked. Try again." }, statusCode: 500);
            }
        });

        app.MapPost("/api/specs/{id}/operator-checks/repair", async (string id, string? project, MoveOperatorChecksRequest request, CancellationToken ct) =>
        {
            var issues = resolve(project);
            if (issues is null || (project is not null && project != issues.ProjectId))
                return Results.NotFound(new { error = "Project not found." });
            if (groomerRuns?.UsesDatabase(issues.Db) != true)
                return Results.Conflict(new { error = "Planning evidence is not available in this project store." });
            try
            {
                var receipt = await new OperatorAcceptanceRepair(issues).ApplyAsync(id, request, "operator", ct);
                return Results.Json(new
                {
                    receipt.Preview.OutcomeId, receipt.AppliedAt,
                    retiredTaskIds = receipt.Preview.SourceRows.Select(r => r.Id),
                    retainedTaskIds = receipt.Preview.TargetRows.Select(r => r.Id),
                    pendingCriteriaIds = receipt.Preview.TransferredCriteria.Select(c => c.Id)
                }, DashboardJson.Options);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to apply operator acceptance repair for {SpecId} in {ProjectId}", id, issues.ProjectId);
                return Results.Json(new { error = "The correction could not be confirmed. Refresh before trying again." }, statusCode: 500);
            }
        });
    }

    private static object Row(IssueRecord row) => new { row.Id, row.Title, row.Description };
}
