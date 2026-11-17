using Forge.Core;
using Forge.Dashboard.Features.Sprints;
using Forge.Dashboard.Features.View;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

public static class SprintReadEndpoints
{
    public static void MapSprintReadEndpoints(WebApplication app, IIssueStore primary, ProjectContextFactory? projects)
    {
        IssueStore? Resolve(string projectId) => projects is null
            ? primary is IssueStore store && projectId == store.ProjectId ? store : null : projects.Find(projectId)?.Issues as IssueStore;

        app.MapGet("/api/sprints/summary", async (string projectId, int? limit, string? cursor, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound(new { error = "project not found" });
            try
            {
                var page = await new SprintReadStore(issues.Db).HistoryAsync(projectId, limit ?? 20, cursor, ct);
                return Results.Ok(new SprintHistoryResponse(projectId, DateTime.UtcNow, Header(page.Active), page.Total,
                    page.History.Select(s => Header(s)!).ToArray(), page.NextCursor));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/api/sprints/board", async (string projectId, string? sprintId, int? limit, string? cursor, CancellationToken ct) =>
        {
            var issues = Resolve(projectId);
            if (issues is null) return Results.NotFound(new { error = "project not found" });
            try
            {
                var page = await new SprintReadStore(issues.Db).BoardAsync(projectId, sprintId, limit ?? 100, cursor, ct);
                if (sprintId is not null && page.Sprint is null) return Results.NotFound(new { error = "sprint not found" });
                return Results.Ok(new SprintBoardResponse(projectId, DateTime.UtcNow, Header(page.Sprint), page.Total,
                    page.Done, page.Members.Select(Card).ToArray(), page.NextCursor));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }
    private static ViewSprint? Header(SprintRecord? sprint) => sprint is null ? null : new(sprint.Id, sprint.Name,
        sprint.Goal, sprint.StartDate, sprint.EndDate, sprint.Status.ToString());
    private static SprintCardRow Card(SprintMemberRead row)
    {
        var issue = row.Issue;
        var situation = TaskSituation.Describe(issue);
        return new(issue.Id, issue.Title, issue.Type, issue.Status.ToString(), issue.GetMetadata("state"), issue.Priority,
            issue.Assignee, issue.GetMetadata("prNumber"), issue.GetMetadata("reworkAttempts"), row.Boosted,
            row.BlockerCount, row.BlockedBy, situation.Text, situation.Tone);
    }
}
