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
    public static void MapAgentRunEndpoints(WebApplication app, AgentRunStore runs)
    {
        app.MapGet("/api/agent-runs", async (string? taskId, CancellationToken ct) =>
        {
            var active = await runs.ListActiveAsync(ct);
            var recent = await runs.ListRecentAsync(limit: 50, taskId: taskId, ct: ct);
            return Results.Json(new
            {
                // The task detail page renders BOTH buckets — the
                // active list must honor the same filter or a
                // concurrent run for an unrelated task shows up as a
                // phantom "running" row (observed live: task-174's
                // CoreDev run rendered on task-167's page).
                active = active.Where(r => taskId is null || r.TaskId == taskId).Select(ToView),
                recent = recent.Select(ToView),
            });
        });

        app.MapGet("/api/agent-runs/{id}", async (string id, CancellationToken ct) =>
        {
            var run = await runs.GetAsync(id, ct);
            if (run is null) return Results.NotFound();
            object transcript = run.TranscriptJson is null
                ? Array.Empty<object>()
                : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(run.TranscriptJson);
            return (IResult)Results.Json(new
            {
                view = ToView(run),
                transcript,
            });
        });
    }

    private static object ToView(AgentRunStore.AgentRunRecord r) => new
    {
        id = r.Id,
        taskId = r.TaskId,
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
    };
}
