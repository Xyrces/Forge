using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// P6 Stage 9 — Engineering Dispatch workflow endpoints.
///   GET   /api/tasks/in-progress           -> full task row + last 10 events
///   POST  /api/tasks/{id}/retry-message   -> inject a string into AgentMessageBus
///   POST  /api/tasks/{id}/recover         -> per-task recovery run
/// </summary>
public static class TaskEndpoints
{
    public sealed record TaskEventDto(string Kind, DateTime At, string? Detail);

    public sealed record InProgressTaskDto(
        string Id,
        string Type,
        string Title,
        string Status,
        int Priority,
        string? Assignee,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? ClosedAt,
        string? DispatchCheckpoint,
        int RecoveryAttempts,
        string? PrUrl,
        string? Branch,
        string? WorktreePath,
        IReadOnlyList<TaskEventDto> Events);

    public static void MapTaskEndpoints(
        WebApplication app,
        IIssueStore issues,
        AgentMessageBus? messageBus,
        Orchestrator.StartupRecovery? recovery,
        ILogger logger,
        Projects.ProjectContextFactory? projectContexts = null,
        ISprintStore? sprints = null)
    {
        // Single-task drill-down: the full row + parsed metadata +
        // the issue_event audit timeline + sprint membership. Powers
        // the /tasks/{id} page; every list view links here.
        app.MapGet("/api/tasks/{id}", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = issues;
            ISprintStore? sprintStore = sprints;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(projectId);
                if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx.Issues;
                sprintStore = ctx.Sprints;
            }
            var t = await store.GetAsync(id, ct);
            if (t is null) return Results.NotFound(new { error = "task_not_found", id });

            var events = await store.ListEventsAsync(t.Id, limit: 100, ct);
            string? sprintId = null, sprintName = null, sprintStatus = null;
            if (sprintStore is not null)
            {
                foreach (var sp in await sprintStore.ListAsync(activeOnly: false, ct))
                {
                    if ((await sprintStore.GetIssueIdsAsync(sp.Id, ct)).Contains(t.Id))
                    {
                        sprintId = sp.Id; sprintName = sp.Name; sprintStatus = sp.Status.ToString();
                        break;
                    }
                }
            }
            return Results.Json(new
            {
                id = t.Id,
                type = t.Type,
                title = t.Title,
                description = t.Description,
                status = t.Status.ToString(),
                priority = t.Priority,
                assignee = t.Assignee,
                parentIssueId = t.ParentIssueId,
                createdAt = t.CreatedAt,
                updatedAt = t.UpdatedAt,
                closedAt = t.ClosedAt,
                dispatchCheckpoint = t.DispatchCheckpoint?.ToString(),
                recoveryAttempts = t.RecoveryAttempts,
                metadata = TaskEndpoints.ParseMetadata(t.MetadataJson),
                sprint = sprintId is null ? null : new { id = sprintId, name = sprintName, status = sprintStatus },
                events = events.Select(e => new TaskEventDto(e.Kind, e.Timestamp, e.Detail)).ToArray(),
            });
        });

        app.MapGet("/api/tasks/in-progress", async (int? limit, string? projectId, CancellationToken ct) =>
        {
            try
            {
                // Multi-project: when ?projectId= is supplied and the
                // factory is available, read from that project's store;
                // otherwise fall back to the injected primary store.
                var store = issues;
                if (projectId is not null && projectContexts is not null)
                {
                    var ctx = projectContexts.Find(projectId);
                    if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                    store = ctx.Issues;
                }
                var inFlight = await store.ListInProgressForRecoveryAsync(ct);
                var rows = new List<InProgressTaskDto>(inFlight.Count);
                foreach (var t in inFlight.Take(limit ?? 100))
                {
                    var events = await store.ListEventsAsync(t.Id, limit: 10, ct);
                    rows.Add(new InProgressTaskDto(
                        Id: t.Id,
                        Type: t.Type,
                        Title: t.Title,
                        Status: t.Status.ToString(),
                        Priority: t.Priority,
                        Assignee: t.Assignee,
                        CreatedAt: t.CreatedAt,
                        UpdatedAt: t.UpdatedAt,
                        ClosedAt: t.ClosedAt,
                        DispatchCheckpoint: t.DispatchCheckpoint?.ToString(),
                        RecoveryAttempts: t.RecoveryAttempts,
                        PrUrl: ExtractMeta(t.MetadataJson, "prUrl"),
                        Branch: ExtractMeta(t.MetadataJson, "branch"),
                        WorktreePath: ExtractMeta(t.MetadataJson, "worktreePath"),
                        Events: events.Select(e => new TaskEventDto(e.Kind, e.Timestamp, e.Detail)).ToArray()));
                }
                return Results.Json(rows);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/tasks/in-progress failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/retry-message", async (string id, HttpContext ctx) =>
        {
            if (messageBus is null) return Results.Problem(detail: "AgentMessageBus not configured", statusCode: 503);
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (!doc.RootElement.TryGetProperty("text", out var textEl))
                    return Results.BadRequest(new { error = "text required" });
                var text = textEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    return Results.BadRequest(new { error = "text cannot be empty" });

                messageBus.Enqueue(id, text);
                return Results.Json(new { accepted = true, taskId = id });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/retry-message failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/requeue", async (string id, string? projectId, HttpContext ctx, CancellationToken ct) =>
        {
            // Operator requeue of a Failed task: the sanctioned path
            // (IssueStore transition + metadata update — never direct
            // SQL). Clears the failure bookkeeping (retryCount,
            // lastError(+At), noProgressAttempts) AND the rework
            // bookkeeping (reworkAttempts/Reason/Context) so both
            // breaker budgets start fresh — requeueing a
            // breaker-tripped task without clearing reworkAttempts
            // would let the next watch sweep re-trip it immediately.
            // Optional JSON body { reason, context }: seeds a guided
            // rework round (the dispatch prompt renders them as
            // "## Rework required") — used when the operator knows
            // exactly what the redispatch must do (e.g. "your PR is
            // approved, the worktree was rebuilt from main; fetch
            // your branch, merge main, push to retrigger CI").
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx2 = projectContexts.Find(projectId);
                if (ctx2 is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx2.Issues;
            }
            string? guideReason = null, guideContext = null;
            try
            {
                if (ctx.Request.ContentLength > 0)
                {
                    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("reason", out var rEl)) guideReason = rEl.GetString();
                        if (doc.RootElement.TryGetProperty("context", out var cEl)) guideContext = cEl.GetString();
                    }
                }
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON body" }); }
            try
            {
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });
                if (t.Status != IssueStatus.Failed)
                    return Results.Conflict(new { error = $"only Failed tasks can be requeued (status is {t.Status})" });

                // One atomic transition: Failed -> Pending + clear the
                // failure bookkeeping so the retry budget starts fresh
                // (upsert-merge only: JSON null is the delete idiom).
                var meta = new Dictionary<string, object>
                {
                    ["retryCount"] = null!,
                    ["noProgressAttempts"] = null!,
                    ["lastError"] = null!,
                    ["lastErrorAt"] = null!,
                    ["reworkAttempts"] = null!,
                    ["reworkReason"] = null!,
                    ["reworkContext"] = null!,
                    ["requeuedFromFailedAt"] = DateTime.UtcNow.ToString("O"),
                };
                if (!string.IsNullOrWhiteSpace(guideReason)) meta["reworkReason"] = guideReason;
                if (!string.IsNullOrWhiteSpace(guideContext)) meta["reworkContext"] = guideContext;
                await store.TransitionAsync(id, IssueStatus.Pending,
                    "operator requeue from Failed (failure + rework bookkeeping cleared)",
                    meta, ct);
                logger.LogInformation("POST /api/tasks/{Id}/requeue: Failed -> Pending, failure + rework metadata cleared (guided={Guided})", id, guideReason is not null);
                return Results.Json(new { taskId = id, status = "Pending" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/requeue failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/adopt-pr", async (string id, AdoptPrRequest? body, string? projectId, CancellationToken ct) =>
        {
            // Adopt an orphan PR into the watch loop: any PR opened
            // outside the pipeline (operator hand-created, external
            // tool, recovered work) gets a proper pr-watch issue so
            // the reviewer/CI/merge loop OWNS it — the sanctioned
            // alternative to hand-merging (operator rule 2026-07-25:
            // no manual out-of-loop fixes).
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(projectId);
                if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = ctx.Issues;
            }
            try
            {
                if (body is null || body.PrNumber <= 0 || string.IsNullOrWhiteSpace(body.Branch))
                    return Results.BadRequest(new { error = "prNumber (> 0) and branch are required" });
                var t = await store.GetAsync(id, ct);
                if (t is null) return Results.NotFound(new { error = "task not found", id });

                var watch = await store.CreateAsync(new NewIssue(
                    Type: AgentTaskTypes.PrWatch,
                    Title: $"Watch PR #{body.PrNumber} for {id}",
                    Description: $"Wait for PR #{body.PrNumber} to be reviewed.",
                    Metadata: new Dictionary<string, object>
                    {
                        ["prNumber"] = body.PrNumber,
                        ["branch"] = body.Branch,
                        ["worktreePath"] = body.WorktreePath ?? string.Empty,
                        ["taskId"] = id,
                        ["adopted"] = "true",
                    }), ct);
                logger.LogInformation("POST /api/tasks/{Id}/adopt-pr: watch {WatchId} created for PR #{Pr}",
                    id, watch.Id, body.PrNumber);
                return Results.Json(new { taskId = id, watchId = watch.Id, prNumber = body.PrNumber });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/adopt-pr failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        app.MapPost("/api/tasks/{id}/recover", async (string id, CancellationToken ct) =>
        {
            if (recovery is null) return Results.Problem(detail: "StartupRecovery not configured", statusCode: 503);
            try
            {
                var reportId = await recovery.RunAsync(specId: null, ct: ct);
                return Results.Json(new { taskId = id, reportId });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "POST /api/tasks/{Id}/recover failed", id);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });
    }

    public sealed record AdoptPrRequest(int PrNumber, string Branch, string? WorktreePath);

    private static Dictionary<string, object?> ParseMetadata(string? metadataJson)
    {
        var d = new Dictionary<string, object?>();
        if (string.IsNullOrEmpty(metadataJson)) return d;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                d[p.Name] = p.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => p.Value.GetString(),
                    System.Text.Json.JsonValueKind.Number => p.Value.GetRawText(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            }
        }
        catch { }
        return d;
    }

    private static string? ExtractMeta(string? metadataJson, string key)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
        }
        catch { }
        return null;
    }
}