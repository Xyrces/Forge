using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

/// <summary>
/// Exposes each project's vision as a source document and a conversational map.
/// </summary>
public static class VisionEndpoints
{
    public static void MapVisionEndpoints(
        WebApplication app,
        VisionStore vision,
        ILogger logger,
        Core.MemoryStore? memory = null,
        Core.IIssueStore? issues = null,
        Projects.ProjectContextFactory? projectContexts = null,
        Core.IVisionConversation? conversation = null)
    {
        var documents = new ProjectVisionDocuments(vision, projectContexts, memory);
        static IResult Snapshot(VisionSnapshot snap, bool scoped) => Results.Json(new
        {
            exists = snap.Exists, path = snap.Path, content = snap.Exists || !scoped ? snap.Content : null,
            lastModified = snap.LastModifiedUtc
        });

        async Task<IResult> InProject(string? id, Func<ProjectVisionFile, Task<IResult>> action)
        {
            try
            {
                var file = documents.Find(id);
                return file is null ? Results.NotFound(new { error = "Project not found.", projectId = id }) : await action(file);
            }
            catch (VisionRevisionConflict ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (VisionPathError ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (VisionMemoryPending ex)
            {
                logger.LogError(ex.InnerException, "Agent guidance synchronization failed after vision save for {Project}", id);
                return Results.Json(new { error = ex.Message, code = "vision_memory_sync_pending" }, statusCode: 503);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Vision document access failed for {Project}", id);
                return Results.Json(new { error = "Forge could not access this project's vision file. Check the project checkout." }, statusCode: 503);
            }
        }

        app.MapGet("/api/vision", (string? projectId, CancellationToken ct) => projectId is null && projectContexts is null
            ? Task.FromResult(Snapshot(vision.Get(), false))
            : InProject(projectId, async file => Snapshot(await documents.ReadAsync(file, ct), projectId is not null)));
        app.MapPost("/api/vision/refresh", (string? projectId, CancellationToken ct) =>
            InProject(projectId, async file => Snapshot(await documents.RefreshAsync(file, ct), projectId is not null)));
        app.MapPut("/api/vision", (VisionUpdate update, string? projectId, CancellationToken ct) =>
            InProject(projectId, async file => Snapshot(await documents.SaveAsync(file, update.BaseRevision, _ => update.Content ?? "", ct), projectId is not null)));

        var group = app.MapGroup("/api/projects/{projectId}/vision");
        group.MapGet("/map", (string projectId, CancellationToken ct) => InProject(projectId, async file =>
            Results.Ok(Core.VisionMap.Parse((await documents.ReadAsync(file, ct)).Content))));
        group.MapGet("/repository", (string projectId) => InProject(projectId, file =>
            Task.FromResult(Snapshot(ProjectVisionDocuments.ReadRepository(file), true))));

        group.MapPost("/chat", (string projectId, VisionChatRequest request, CancellationToken ct) => InProject(projectId, async file =>
        {
            if (request.Messages is not { Count: > 0 } || request.Pending is null
                || request.Messages.Any(m => m is null || m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Text))
                || request.Messages[^1].Role != "user" || request.Pending.Any(e => e is null))
                return Results.BadRequest(new { error = "Provide the conversation ending in your message and the current draft." });
            var map = Core.VisionMap.Parse((await documents.ReadAsync(file, ct)).Content);
            if (map.Revision != request.BaseRevision) throw new VisionRevisionConflict();
            if (conversation is null) return Results.Json(new { error = "The vision editor's model is unavailable." }, statusCode: 503);
            try { if (request.Pending.Count > 0) Core.VisionMap.Apply(map.Source, map.Revision, request.Pending); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            try
            {
                var reply = await conversation.ReplyAsync(file.ProjectId!, map, request.Messages, request.Pending, ct);
                if ((await documents.ReadAsync(file, ct)).Content != map.Source) throw new VisionRevisionConflict();
                return Results.Ok(reply);
            }
            catch (Core.Control.BudgetAdmissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (VisionRevisionConflict) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Vision conversation failed for {Project}", projectId);
                return Results.Json(new { error = "The vision editor could not complete this turn. Your draft is available to retry." }, statusCode: 502);
            }
        }));

        group.MapPost("/apply", (string projectId, VisionApplyRequest request, CancellationToken ct) => InProject(projectId, async file =>
        {
            if (string.IsNullOrWhiteSpace(request.BaseRevision) || request.Edits is not { Count: > 0 } || request.Edits.Any(e => e is null))
                return Results.BadRequest(new { error = "Provide a source revision and at least one proposed change." });
            try
            {
                var saved = await documents.SaveAsync(file, request.BaseRevision,
                    source => Core.VisionMap.Apply(source, request.BaseRevision, request.Edits), ct);
                return Results.Ok(Core.VisionMap.Parse(saved.Content));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }));

        if (issues is not null) MapDraftRequest(app, issues, logger, projectContexts);
    }

    public sealed record VisionUpdate(string? Content, string? BaseRevision = null);
    public sealed record VisionChatRequest(string BaseRevision, IReadOnlyList<Core.VisionChatMessage> Messages,
        IReadOnlyList<Core.VisionMapEdit> Pending);
    public sealed record VisionApplyRequest(string BaseRevision, IReadOnlyList<Core.VisionMapEdit> Edits);

    /// <summary>
    /// "Draft from codebase": enqueues an ad-hoc task asking an
    /// engineering agent to analyze the repo and write the vision
    /// document. The task is deliberately parentless + ungroomed —
    /// it flows through technical grooming (vision/current-state
    /// check) and the normal sprint machinery like everything else.
    /// No special scheduler, no special progress tracking: the
    /// pipeline IS the tracker.
    /// </summary>
    private static void MapDraftRequest(WebApplication app, Core.IIssueStore issues, ILogger logger,
        Projects.ProjectContextFactory? projectContexts)
    {
        app.MapPost("/api/vision/draft-request", async (string? projectId, CancellationToken ct) =>
        {
            // Project lens (live misroute 2026-08-21: the PH vision
            // board's draft button filed the task into the PRIMARY
            // store with a Forge-shaped prompt — it would have drafted
            // Forge's MASTER_DESIGN, not PortHorizon's). The task goes
            // to the viewed project's own store.
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var pctx = projectContexts.Find(projectId);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = pctx.Issues;
            }
            var pending = await store.ListAsync(new Core.IssueFilter { Status = Core.IssueStatus.Pending }, ct);
            var inProgress = await store.ListAsync(new Core.IssueFilter { Status = Core.IssueStatus.InProgress }, ct);
            if (pending.Concat(inProgress).Any(i =>
                    string.Equals(i.GetMetadata("visionDraft"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Conflict(new { error = "vision_draft_already_open" });
            }

            var issue = await store.CreateAsync(new Core.NewIssue(
                Type: "task",
                Title: "Draft docs/MASTER_DESIGN.md from codebase analysis",
                Description: """
                    Analyze this repository and draft the project's master design document at
                    docs/MASTER_DESIGN.md (the path the dashboard Vision page renders and the
                    vision memory key injects into every agent prompt).

                    Cover: what the system is and who it serves; the module map and
                    non-negotiable boundaries; the core data + dispatch flows; the major
                    subsystems; and the north-star direction future work should align to.
                    Ground every claim in the actual code (cite paths); do not invent
                    roadmap items. Read the repository's README and top-level docs first
                    (whatever this repo ships — e.g. README.md, AGENTS.md, docs/), then the
                    source.
                    """,
                Priority: 2,
                Metadata: new Dictionary<string, object>
                {
                    ["source"] = "vision-draft-button",
                    ["visionDraft"] = "true",
                }), ct);
            logger.LogInformation("Vision draft requested (project={Project}): task {Id} filed (awaits grooming)",
                projectId ?? "<primary>", issue.Id);
            return Results.Json(new { taskId = issue.Id, status = "pending_grooming" });
        });
    }
}
