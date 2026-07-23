using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

/// <summary>
/// P0.5: HTTP endpoints for the vision store. The dashboard's
/// Vision tab fetches <c>GET /api/vision</c> on load and after the
/// "Refresh" button; the JSON response carries the loaded content
/// + metadata.
/// </summary>
public static class VisionEndpoints
{
    public static void MapVisionEndpoints(
        WebApplication app,
        VisionStore vision,
        ILogger logger,
        Core.MemoryStore? memory = null,
        Core.IIssueStore? issues = null)
    {
        app.MapGet("/api/vision", () =>
        {
            var snap = vision.Get();
            return Results.Json(new
            {
                exists = snap.Exists,
                path = snap.Path,
                content = snap.Content,
                lastModified = snap.LastModifiedUtc,
            });
        });

        app.MapPost("/api/vision/refresh", async (CancellationToken ct) =>
        {
            var snap = vision.Reload();
            logger.LogInformation("Vision refreshed: exists={Exists} path={Path}", snap.Exists, snap.Path);
            // Keep the prompt-injection key in step with the file —
            // this is also the "doc landed via PR merge + project
            // sync" path, so refresh must re-inject, not just re-read.
            if (memory is not null && snap.Exists)
                await memory.RememberAsync("vision/master", snap.Content, ttlDays: null, ct);
            return Results.Json(new
            {
                exists = snap.Exists,
                path = snap.Path,
                content = snap.Content,
                lastModified = snap.LastModifiedUtc,
            });
        });

        // Dashboard editor save path. Writes the file (creating it
        // if missing) and refreshes the vision/master memory key so
        // subsequent agent runs see the new vision immediately.
        app.MapPut("/api/vision", async (VisionUpdate update, CancellationToken ct) =>
        {
            var snap = vision.Write(update.Content ?? "");
            logger.LogInformation("Vision saved: {Path} ({Len} chars)", snap.Path, snap.Content.Length);
            if (memory is not null && snap.Exists)
                await memory.RememberAsync("vision/master", snap.Content, ttlDays: null, ct);
            return Results.Json(new
            {
                exists = snap.Exists,
                path = snap.Path,
                content = snap.Content,
                lastModified = snap.LastModifiedUtc,
            });
        });

        if (issues is not null)
        {
            MapDraftRequest(app, issues, logger);
        }
    }

    public sealed record VisionUpdate(string? Content);

    /// <summary>
    /// "Draft from codebase": enqueues an ad-hoc task asking an
    /// engineering agent to analyze the repo and write the vision
    /// document. The task is deliberately parentless + ungroomed —
    /// it flows through technical grooming (vision/current-state
    /// check) and the normal sprint machinery like everything else.
    /// No special scheduler, no special progress tracking: the
    /// pipeline IS the tracker.
    /// </summary>
    private static void MapDraftRequest(WebApplication app, Core.IIssueStore issues, ILogger logger)
    {
        app.MapPost("/api/vision/draft-request", async (CancellationToken ct) =>
        {
            var pending = await issues.ListAsync(new Core.IssueFilter { Status = Core.IssueStatus.Pending }, ct);
            var inProgress = await issues.ListAsync(new Core.IssueFilter { Status = Core.IssueStatus.InProgress }, ct);
            if (pending.Concat(inProgress).Any(i =>
                    string.Equals(i.GetMetadata("visionDraft"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Conflict(new { error = "vision_draft_already_open" });
            }

            var issue = await issues.CreateAsync(new Core.NewIssue(
                Type: "task",
                Title: "Draft docs/MASTER_DESIGN.md from codebase analysis",
                Description: """
                    Analyze this repository and draft the project's master design document at
                    docs/MASTER_DESIGN.md (the path the dashboard Vision page renders and the
                    vision/master memory key injects into every agent prompt).

                    Cover: what the system is and who it serves; the module map and
                    non-negotiable boundaries; the core data + dispatch flows; the major
                    subsystems (pipeline stages, review loop, recovery, dashboard); and the
                    north-star direction future work should align to. Ground every claim in
                    the actual code (cite paths); do not invent roadmap items. Read
                    AGENTS.md, README.md, docs/system-flow.md first, then the source.
                    """,
                Priority: 2,
                Metadata: new Dictionary<string, object>
                {
                    ["source"] = "vision-draft-button",
                    ["visionDraft"] = "true",
                }), ct);
            logger.LogInformation("Vision draft requested: task {Id} filed (awaits grooming)", issue.Id);
            return Results.Json(new { taskId = issue.Id, status = "pending_grooming" });
        });
    }
}