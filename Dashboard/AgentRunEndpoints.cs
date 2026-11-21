using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// Agent run observability: active + recent runs (who is doing
/// what) and full run transcripts (see their work — the complete
/// conversation with tool calls and results).
/// </summary>
public static class AgentRunEndpoints
{
    public static void MapAgentRunEndpoints(
        WebApplication app,
        AgentRunStore runs,
        Projects.ProjectContextFactory? projectContexts = null,
        IIssueStore? primaryIssues = null)
    {
        // Multi-project: agent_run is per-project workload data — the
        // runner writes each run to the OWNING project's schema
        // (operator rule 2026-07-30). ?projectId= reads that project's
        // run store; absent = primary. Unknown project 404s. (Runs
        // pre-dating the per-project writers live in the primary
        // store with project_id NULL — the "legacy" badge.)
        AgentRunStore? ResolveRuns(string? projectId, out IResult? error)
        {
            error = null;
            if (projectId is null || projectContexts is null) return runs;
            var ctx = projectContexts.Find(projectId);
            if (ctx is null)
            {
                error = Results.NotFound(new { error = "project not found", projectId });
                return null;
            }
            return new AgentRunStore(((IssueStore)ctx.Issues).Db);
        }

        app.MapGet("/api/agent-runs", async (string? taskId, string? projectId, CancellationToken ct) =>
        {
            var store = ResolveRuns(projectId, out var err);
            if (err is not null) return err;
            // The resolved store IS the project scope — no column
            // filter (a filter would hide legacy project_id NULL rows
            // stranded in the primary store).
            var active = await store!.ListActiveAsync(ct, taskId);
            var recent = await store.ListRecentAsync(limit: 50, taskId: taskId, ct: ct);
            var issues = projectId is null ? primaryIssues : projectContexts?.Find(projectId)?.Issues;
            var titles = new Dictionary<string, string?>();
            foreach (var id in active.Concat(recent).Select(r => r.TaskId).OfType<string>().Distinct())
                titles[id] = issues is null ? null : (await issues.GetAsync(id, ct))?.Title;
            return Results.Json(new
            {
                // The task detail page renders BOTH buckets — the
                // active list must honor the same filter or a
                // concurrent run for an unrelated task shows up as a
                // phantom "running" row (observed live: task-174's
                // CoreDev run rendered on task-167's page).
                active = active
                    .Where(r => taskId is null || r.TaskId == taskId)
                    .Select(r => ToView(r, r.TaskId is { } id ? titles.GetValueOrDefault(id) : null, projectId)),
                recent = recent.Select(r => ToView(r, r.TaskId is { } id ? titles.GetValueOrDefault(id) : null, projectId)),
                historyLimit = 50,
            });
        });

        app.MapGet("/api/agent-runs/{id}", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = ResolveRuns(projectId, out var err);
            if (err is not null) return err;
            var run = await store!.GetAsync(id, ct);
            if (run is null) return Results.NotFound();
            var issues = projectId is null ? primaryIssues : projectContexts?.Find(projectId)?.Issues;
            var title = issues is not null && run.TaskId is not null ? (await issues.GetAsync(run.TaskId, ct))?.Title : null;
            object transcript = run.TranscriptJson is null
                ? Array.Empty<object>()
                : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(run.TranscriptJson);
            return (IResult)Results.Json(new
            {
                view = ToView(run, title, projectId),
                transcript,
            });
        });
    }

    private static object ToView(AgentRunStore.AgentRunRecord r, string? title = null, string? scope = null) => new
    {
        id = r.Id,
        taskId = r.TaskId,
        taskTitle = title,
        scopeProjectId = scope ?? r.ProjectId,
        role = r.Role,
        model = r.Model,
        status = r.Status,
        startedAt = r.StartedAt,
        finishedAt = r.FinishedAt,
        durationMs = r.DurationMs,
        messageCount = r.MessageCount,
        toolCallCount = r.ToolCallCount,
        textChars = r.TextChars,
        error = r.Error,
        hasTranscript = r.TranscriptJson is not null,
        lastActivityAt = r.LastActivityAt,
        phase = r.Phase,
        resumedSession = r.ResumedSession,
        projectId = r.ProjectId,
        inputTokens = r.InputTokens,
        outputTokens = r.OutputTokens,
        cacheReadTokens = r.CacheReadTokens,
        currentContextTokens = r.CurrentContextTokens,
    };
}
