using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Core.Workflow;

namespace Forge.Dashboard;

/// <summary>
/// Editable workflow (pass 2): draft + publish control surface for
/// the pipeline definition rendered on the Flow page. Draft lives at
/// memory key <c>workflow/draft</c>; publish validates (fail closed),
/// snapshots the previous live definition (<c>workflow/versions/</c>,
/// newest 10 kept), and overwrites <c>workflow/live</c>. Resolution
/// per read — no restart; machinery picks changes up at its next
/// evaluation. All state is memory keys: no schema change, bounded
/// growth.
/// </summary>
public static class WorkflowEndpoints
{
    private const int MaxVersions = 10;

    public static void MapWorkflowEndpoints(
        WebApplication app,
        MemoryStore memory,
        IDashboardEventBus events,
        ILogger logger)
    {
        var resolver = new WorkflowResolver(memory);

        app.MapGet("/api/workflow", async (CancellationToken ct) =>
        {
            var live = await resolver.ResolveAsync(ct);
            var draftBody = (await memory.RecallAsync(WorkflowResolver.DraftKey, ct)).FirstOrDefault()?.Body;
            var draft = WorkflowResolver.TryParse(draftBody);
            return Results.Json(new
            {
                live,
                draft,
                hasDraft = draft is not null,
                diff = draft is not null ? WorkflowValidator.Diff(live, draft) : Array.Empty<string>(),
            }, DashboardJson.Options);
        });

        app.MapGet("/api/workflow/default", (CancellationToken _) =>
            Results.Json(WorkflowDefaults.Definition, DashboardJson.Options));

        app.MapPut("/api/workflow/draft", async (HttpRequest req, CancellationToken ct) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync(ct);
            var parsed = WorkflowResolver.TryParse(body);
            if (parsed is null)
            {
                return Results.BadRequest(new { error = "invalid_definition", detail = "body is not a parseable workflow definition" });
            }
            // Normalize: store the parsed form, not the raw text.
            await memory.RememberAsync(WorkflowResolver.DraftKey, WorkflowResolver.Serialize(parsed), ttlDays: null, ct);
            var live = await resolver.ResolveAsync(ct);
            return Results.Json(new { ok = true, diff = WorkflowValidator.Diff(live, parsed) }, DashboardJson.Options);
        });

        app.MapDelete("/api/workflow/draft", async (CancellationToken ct) =>
        {
            await memory.ForgetAsync(WorkflowResolver.DraftKey, ct);
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/workflow/publish", async (CancellationToken ct) =>
        {
            var draftBody = (await memory.RecallAsync(WorkflowResolver.DraftKey, ct)).FirstOrDefault()?.Body;
            var draft = WorkflowResolver.TryParse(draftBody);
            if (draft is null)
            {
                return Results.BadRequest(new { error = "no_draft", detail = "nothing to publish — save a draft first" });
            }
            var errors = WorkflowValidator.Validate(draft);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { error = "validation_failed", errors });
            }

            // Snapshot the previous live definition (empty body marks
            // "was the built-in default") before overwriting.
            var previousLive = (await memory.RecallAsync(WorkflowResolver.LiveKey, ct)).FirstOrDefault()?.Body ?? "";
            await SnapshotAsync(memory, previousLive, ct);

            await memory.RememberAsync(WorkflowResolver.LiveKey, WorkflowResolver.Serialize(draft), ttlDays: null, ct);
            await memory.ForgetAsync(WorkflowResolver.DraftKey, ct);

            var live = await resolver.ResolveAsync(ct);
            var diff = WorkflowValidator.Diff(WorkflowResolver.TryParse(previousLive) ?? WorkflowDefaults.Definition, draft);
            var summary = diff.Count == 0 ? "no effective changes" : string.Join("; ", diff);
            events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.WorkflowPublished,
                "workflow", $"Workflow published: {summary}"));
            logger.LogInformation("Workflow definition published: {Summary}", summary);
            return Results.Json(new { ok = true, live, diff }, DashboardJson.Options);
        });

        app.MapGet("/api/workflow/versions", async (CancellationToken ct) =>
        {
            var versions = (await memory.RecallAsync(WorkflowResolver.VersionsPrefix, ct))
                .Select(v => new
                {
                    key = v.Key,
                    publishedAt = v.CreatedAt,
                    isDefaultSnapshot = v.Body.Length == 0,
                    definition = WorkflowResolver.TryParse(v.Body),
                })
                .OrderByDescending(v => v.publishedAt)
                .ToList();
            return Results.Json(versions, DashboardJson.Options);
        });

        app.MapPost("/api/workflow/versions/restore", async ([FromBody] RestoreRequest request, CancellationToken ct) =>
        {
            var versions = await memory.RecallAsync(WorkflowResolver.VersionsPrefix, ct);
            var version = versions.FirstOrDefault(v => string.Equals(v.Key, request.Key, StringComparison.Ordinal));
            if (version is null)
            {
                return Results.BadRequest(new { error = "unknown_version", request.Key });
            }

            var previousLive = (await memory.RecallAsync(WorkflowResolver.LiveKey, ct)).FirstOrDefault()?.Body ?? "";
            await SnapshotAsync(memory, previousLive, ct);

            if (version.Body.Length == 0)
            {
                // Snapshot of "no override" — restore means back to default.
                await memory.ForgetAsync(WorkflowResolver.LiveKey, ct);
            }
            else
            {
                await memory.RememberAsync(WorkflowResolver.LiveKey, version.Body, ttlDays: null, ct);
            }
            await memory.ForgetAsync(WorkflowResolver.DraftKey, ct);

            var restored = version.Body.Length == 0
                ? "the built-in default"
                : $"snapshot {version.Key}";
            events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.WorkflowPublished,
                "workflow", $"Workflow restored to {restored}"));
            logger.LogInformation("Workflow definition restored to {Restored}", restored);
            var live = await resolver.ResolveAsync(ct);
            return Results.Json(new { ok = true, live }, DashboardJson.Options);
        });
    }

    private static async Task SnapshotAsync(MemoryStore memory, string previousLiveBody, CancellationToken ct)
    {
        var key = WorkflowResolver.VersionsPrefix + DateTime.UtcNow.Ticks;
        await memory.RememberAsync(key, previousLiveBody, ttlDays: null, ct);
        // Prune to the newest MaxVersions. Keys are tick-ordered.
        var all = (await memory.RecallAsync(WorkflowResolver.VersionsPrefix, ct))
            .OrderByDescending(v => v.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var stale in all.Skip(MaxVersions))
        {
            await memory.ForgetAsync(stale.Key, ct);
        }
    }

    private sealed record RestoreRequest(string Key);
}
