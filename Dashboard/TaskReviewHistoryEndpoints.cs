using Forge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

public static class TaskReviewHistoryEndpoints
{
    public static void Map(WebApplication app, IIssueStore primary, Projects.ProjectContextFactory? projects, Func<string, GitHubService?>? github)
    {
        app.MapGet("/api/tasks/{id}/review-history", async (string id, string projectId, CancellationToken ct) =>
        {
            var store = projects?.Find(projectId)?.Issues ?? (primary is IssueStore p && p.ProjectId == projectId ? p : null);
            var issue = store is null ? null : await store.GetAsync(id, ct);
            if (issue is null) return Results.NotFound();
            if (!int.TryParse(issue.GetMetadata("prNumber"), out var pr)) return Results.Ok(new LegacyReviewHistoryPage([], false, ["No PR is recorded for this task."]));
            var service = github?.Invoke(projectId);
            if (service is null) return Results.StatusCode(503);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { return Results.Ok(await service.ReadReviewHistoryAsync(pr, timeout.Token)); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.Problem("Review history timed out. Cached task evidence remains available.", statusCode: 504); }
            catch (Octokit.ApiException) { return Results.Problem("GitHub review history is unavailable or not accessible. The stored task evidence is unchanged.", statusCode: 502); }
        });
    }
}
