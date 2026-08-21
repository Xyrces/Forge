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
        Core.IIssueStore? issues = null,
        Projects.ProjectContextFactory? projectContexts = null)
    {
        // Project lens (operator rule: everything but /now is
        // per-project): ?projectId= resolves the vision from THAT
        // project's root + memory key vision/<projectId>; unscoped
        // keeps the legacy primary-project behavior.
        (bool Found, string? Root, Core.IIssueStore Store, string VisionKey) Resolve(string? projectId)
        {
            if (projectId is null || projectContexts is null)
                return (projectId is null, null, issues!, "vision/master");
            var pctx = projectContexts.Find(projectId);
            return pctx is null
                ? (false, null, issues!, "vision/master")
                : (true, pctx.Options.Root, pctx.Issues, $"vision/{projectId}");
        }

        app.MapGet("/api/vision", (string? projectId) =>
        {
            if (projectId is null || projectContexts is null)
            {
                var snap = vision.Get();
                return Results.Json(new
                {
                    exists = snap.Exists,
                    path = snap.Path,
                    content = snap.Content,
                    lastModified = snap.LastModifiedUtc,
                });
            }
            var r = Resolve(projectId);
            if (!r.Found) return Results.NotFound(new { error = "project not found", projectId });
            var file = Path.Combine(r.Root!, vision.RelativePath);
            if (!File.Exists(file))
            {
                return Results.Json(new { exists = false, path = file, content = (string?)null, lastModified = (DateTime?)null });
            }
            var content = File.ReadAllText(file);
            return Results.Json(new
            {
                exists = true,
                path = file,
                content,
                lastModified = (DateTime?)File.GetLastWriteTimeUtc(file),
            });
        });

        app.MapPost("/api/vision/refresh", async (string? projectId, CancellationToken ct) =>
        {
            if (projectId is not null && projectContexts is not null)
            {
                var r = Resolve(projectId);
                if (!r.Found) return Results.NotFound(new { error = "project not found", projectId });
                var file = Path.Combine(r.Root!, vision.RelativePath);
                var exists = File.Exists(file);
                var content = exists ? File.ReadAllText(file) : null;
                logger.LogInformation("Vision refreshed (project={Project}): exists={Exists} path={Path}", projectId, exists, file);
                if (memory is not null && exists)
                    await memory.RememberAsync(r.VisionKey, content!, ttlDays: null, ct);
                return Results.Json(new
                {
                    exists,
                    path = file,
                    content,
                    lastModified = exists ? (DateTime?)File.GetLastWriteTimeUtc(file) : null,
                });
            }
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
        // if missing) and refreshes the vision/<projectId> memory key
        // so subsequent agent runs see the new vision immediately.
        app.MapPut("/api/vision", async (VisionUpdate update, string? projectId, CancellationToken ct) =>
        {
            if (projectId is not null && projectContexts is not null)
            {
                var r = Resolve(projectId);
                if (!r.Found) return Results.NotFound(new { error = "project not found", projectId });
                var file = Path.Combine(r.Root!, vision.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, update.Content ?? "");
                logger.LogInformation("Vision saved (project={Project}): {Path} ({Len} chars)", projectId, file, (update.Content ?? "").Length);
                if (memory is not null)
                    await memory.RememberAsync(r.VisionKey, update.Content ?? "", ttlDays: null, ct);
                return Results.Json(new
                {
                    exists = true,
                    path = file,
                    content = update.Content ?? "",
                    lastModified = (DateTime?)File.GetLastWriteTimeUtc(file),
                });
            }
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
            MapDraftRequest(app, issues, logger, projectContexts);
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