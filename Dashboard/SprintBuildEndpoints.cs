using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// GET /api/sprints/building — the inter-sprint build state
/// (operator request 2026-08-06). The SprintAssembler snapshots the
/// between-sprints phase (triage → materialization → grooming →
/// assembly) to the project's memory store every tick; this reads it
/// back so the Sprints page can show WHY there's no active sprint
/// instead of looking stuck. Unknown projects 404 (strict
/// multi-project rule); a project with no snapshot yet reports
/// phase=unknown.
/// </summary>
public static class SprintBuildEndpoints
{
    public static void Map(
        WebApplication app,
        IIssueStore issues,
        Projects.ProjectContextFactory? projectContexts = null)
    {
        app.MapGet("/api/sprints/building", async (string? projectId, CancellationToken ct) =>
        {
            var issueStore = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var pctx = projectContexts.Find(projectId);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                issueStore = pctx.Issues;
            }
            if (issueStore is not IssueStore concrete)
            {
                return Results.Json(new { phase = "unknown", reason = "build state unavailable for this store" });
            }
            var mem = new MemoryStore(concrete.Db);
            var hit = (await mem.RecallAsync(SprintBuildStateKeys.BuildStateKey, ct))
                .FirstOrDefault(m => string.Equals(m.Key, SprintBuildStateKeys.BuildStateKey, StringComparison.Ordinal));
            if (hit is null)
            {
                return Results.Json(new { phase = "unknown", reason = "no assembler tick recorded yet" });
            }
            return Results.Text(hit.Body, "application/json");
        });
    }
}
