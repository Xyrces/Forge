using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// P1.5.a endpoints: spec CRUD + version history.
///
/// <para>
/// P1.5.a is read-only from the dashboard UI (no edit controls). The
/// endpoints are full CRUD so the Product agent (P1.5.b) and the
/// future groomer can write to specs; the dashboard tab just shows
/// what's there.
/// </para>
/// </summary>
public static class SpecEndpoints
{
    public static void MapSpecEndpoints(
        WebApplication app,
        ISpecStore specs,
        ISpecExtractionReader extractor,
        ILogger logger,
        Forge.Core.IIntakeStore? intakeStore = null,
        GroomerAgentFactory? groomerFactory = null,
        IssueGroomerRunStore? groomerRuns = null,
        Projects.ProjectContextFactory? projectContexts = null,
        Forge.Core.IIssueStore? issues = null)
    {
        // Multi-project: when ?project= names a registered project and the
        // factory is available, read from THAT project's spec store (spec
        // rows live in the per-project issues sqlite file). Absent param
        // keeps the legacy behavior: the injected primary store, filtered
        // by the project_id column.
        ISpecStore ResolveSpecs(string? project)
        {
            if (project is not null && projectContexts is not null)
            {
                var ctx = projectContexts.Find(project);
                if (ctx is not null) return ctx.Specs;
            }
            return specs;
        }

        // Multi-project id-addressed routes: spec ids are per-project
        // sequences, so without ?project= spec-5 resolves to the
        // PRIMARY project's spec-5. An explicit but unknown project
        // 404s rather than silently falling back to the primary.
        (ISpecStore Specs, Forge.Core.IIssueStore? Issues, ISpecExtractionReader Extractor)? ResolveOwned(string? project)
        {
            if (project is null || projectContexts is null)
                return (specs, issues, extractor);
            var ctx = projectContexts.Find(project);
            if (ctx is null) return null;
            var ex = extractor is SpecExtractionReader && ctx.Issues is Forge.Core.IssueStore concrete
                ? new SpecExtractionReader(concrete)
                : extractor;
            return (ctx.Specs, ctx.Issues, ex);
        }

        app.MapGet("/api/specs", async (string? project, string? status, CancellationToken ct) =>
        {
            SpecStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<SpecStatus>(status, ignoreCase: true, out var s))
                statusFilter = s;
            // When reading from a resolved per-project store, don't also
            // filter by the project_id column (rows in a per-project DB
            // already belong to that project; legacy rows may carry a
            // stale project_id). The legacy single-store path still
            // filters by column.
            var resolved = ResolveSpecs(project);
            var columnFilter = ReferenceEquals(resolved, specs) ? project : null;
            var list = await resolved.ListAsync(columnFilter, statusFilter, ct);
            return Results.Json(list.Select(ToSpecView).ToArray(), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var spec = await owned.Value.Specs.GetAsync(id, ct);
            return spec is null
                ? Results.NotFound()
                : Results.Json(ToSpecView(spec), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}/versions", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var versions = await owned.Value.Specs.ListVersionsAsync(id, ct);
            return Results.Json(versions.Select(ToVersionView).ToArray(), DashboardJson.Options);
        });

        // Spec drill-down: the decomposition tree the groomer
        // produced (stories -> tasks, via parent_issue_id) plus the
        // groom-run history. Powers the /specs/{id} detail page.
        app.MapGet("/api/specs/{id}/tree", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var spec = await owned.Value.Specs.GetAsync(id, ct);
            if (spec is null) return Results.NotFound();

            var storyViews = new List<object>();
            var orphanTaskViews = new List<object>();
            var groomRunViews = new List<object>();
            if (owned.Value.Issues is not null)
            {
                var all = await owned.Value.Issues.ListAsync(new Forge.Core.IssueFilter(), ct);
                var stories = all.Where(i => string.Equals(i.Type, "story", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(i.ParentIssueId, spec.Id, StringComparison.Ordinal)).ToList();
                var tasks = all.Where(i => !Forge.Core.AgentTaskTypes.IsContainer(i.Type)
                    && i.Type != Forge.Core.AgentTaskTypes.PrWatch).ToList();
                foreach (var story in stories)
                {
                    var children = tasks.Where(t => string.Equals(t.ParentIssueId, story.Id, StringComparison.Ordinal));
                    storyViews.Add(new
                    {
                        id = story.Id,
                        title = story.Title,
                        status = story.Status.ToString(),
                        tasks = children.Select(ToTaskView).ToArray(),
                    });
                }
                // Tasks parented directly to the spec (no story) —
                // unusual, but show them rather than dropping them.
                foreach (var t in tasks.Where(t => string.Equals(t.ParentIssueId, spec.Id, StringComparison.Ordinal)))
                {
                    orphanTaskViews.Add(ToTaskView(t));
                }
            }
            if (groomerRuns is not null)
            {
                var runs = await groomerRuns.ListAsync(specId: spec.Id, limit: 20, ct);
                foreach (var r in runs)
                {
                    groomRunViews.Add(new
                    {
                        ts = r.Ts,
                        trigger = r.Trigger.ToString(),
                        status = r.Status.ToString(),
                        storiesProduced = r.StoriesProduced,
                        tasksProduced = r.TasksProduced,
                        error = r.Error,
                        durationMs = r.DurationMs,
                    });
                }
            }
            return Results.Json(new
            {
                spec = ToSpecView(spec),
                stories = storyViews,
                orphanTasks = orphanTaskViews,
                groomRuns = groomRunViews,
            }, DashboardJson.Options);
        });

        // Phase 2b: extracted-tables reads. The dashboard's Spec
        // side-panel renders diagrams from spec_diagram; the Graph
        // tab reads spec_touches; the Deps tab reads spec_dep.
        app.MapGet("/api/specs/{id}/diagrams", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var diagrams = await owned.Value.Extractor.GetDiagramsAsync(id, ct);
            return Results.Json(diagrams.Select(d => new
            {
                specId = d.SpecId,
                ordinal = d.Ordinal,
                kind = d.Kind,
                source = d.Source,
                title = d.Title
            }).ToArray(), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}/touches", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var touches = await owned.Value.Extractor.GetTouchesAsync(id, ct);
            return Results.Json(touches.Select(t => new
            {
                specId = t.SpecId,
                moduleId = t.ModuleId,
                source = t.Source,
                rationale = t.Rationale,
                createdAt = t.CreatedAt
            }).ToArray(), DashboardJson.Options);
        });

        app.MapGet("/api/specs/{id}/deps", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var deps = await owned.Value.Extractor.GetDepsAsync(id, ct);
            return Results.Json(deps.Select(d => new
            {
                fromSpecId = d.FromSpecId,
                toSpecId = d.ToSpecId,
                kind = d.Kind,
                rationale = d.Rationale,
                source = d.Source,
                createdAt = d.CreatedAt
            }).ToArray(), DashboardJson.Options);
        });

        // Phase 2b: lookup specs produced by a specific intake
        // session. Used by the intake tab side-panel to render the
        // master + children of an in-progress intake.
        app.MapGet("/api/intake/sessions/{sessionId}/specs", async (string sessionId, CancellationToken ct) =>
        {
            if (intakeStore is null) return Results.Json(Array.Empty<object>(), DashboardJson.Options);
            var session = await intakeStore.GetAsync(sessionId, ct);
            if (session is null) return Results.NotFound();
            var proposed = session.Messages
                .Where(m => m.ProposedEpicId is not null)
                .Select(m => m.ProposedEpicId!)
                .Distinct()
                .ToList();
            var allSpecs = new List<SpecRecord>();
            foreach (var pid in proposed)
            {
                var match = await extractor.ListByParentIssueIdAsync(pid, ct);
                allSpecs.AddRange(match);
            }
            return Results.Json(allSpecs.Select(ToSpecView).ToArray(), DashboardJson.Options);
        });

        app.MapPost("/api/specs", async (HttpContext ctx) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewSpec>(ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.ProjectId) || string.IsNullOrWhiteSpace(spec.Title))
                return Results.BadRequest(new { error = "projectId and title required" });
            // Planning-lane routing: the row belongs to the store
            // OWNED by spec.ProjectId, never the primary store.
            var store = specs;
            if (projectContexts is not null)
            {
                var owned = projectContexts.Find(spec.ProjectId);
                if (owned is null)
                    return Results.BadRequest(new { error = "unknown project", projectId = spec.ProjectId });
                store = owned.Specs;
            }
            try
            {
                var created = await store.CreateAsync(spec, ctx.RequestAborted);
                return Results.Json(ToSpecView(created), DashboardJson.Options, statusCode: 201);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PATCH supports two operations: replace the body (creates a new
        // version) OR change the status. The request body's `op` field
        // picks which one: "update_body" or "set_status".
        app.MapPatch("/api/specs/{id}", async (string id, HttpContext ctx, string? project) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "expected object body" });
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var opEl))
                return Results.BadRequest(new { error = "op required ('update_body' or 'set_status')" });
            var op = opEl.GetString();
            try
            {
                if (op == "set_status" && root.TryGetProperty("status", out var stEl)
                    && Enum.TryParse<SpecStatus>(stEl.GetString() ?? "", ignoreCase: true, out var newStatus))
                {
                    var updated = await owned.Value.Specs.SetStatusAsync(id, newStatus, ctx.RequestAborted);
                    return Results.Json(ToSpecView(updated), DashboardJson.Options);
                }
                if (op == "update_body" && root.TryGetProperty("body", out var bodyEl))
                {
                    var bodyText = bodyEl.GetString() ?? "";
                    var author = root.TryGetProperty("author", out var aEl) ? aEl.GetString() : null;
                    var updated = await owned.Value.Specs.UpdateBodyAsync(id, new UpdateSpecBody(bodyText, author), ctx.RequestAborted);
                    return Results.Json(ToSpecView(updated), DashboardJson.Options);
                }
                return Results.BadRequest(new { error = "unknown op (use 'update_body' or 'set_status')" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/specs/{id}", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            await owned.Value.Specs.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // Phase 3.5: operator-triggered grooming. Returns immediately;
        // the agent runs on a worker thread and emits dashboard events
        // (groomer.run.started / completed / failed) as it works.
        if (groomerFactory is not null)
        {
            app.MapPost("/api/specs/{id}/groom", async (string id, string? force, string? project, CancellationToken ct) =>
            {
                var owned = ResolveOwned(project);
                if (owned is null) return Results.NotFound(new { error = "project not found", project });
                var spec = await owned.Value.Specs.GetAsync(id, ct);
                if (spec is null)
                    return Results.NotFound(new { error = "spec_not_found" });
                // P2.a: the manual groom endpoint now accepts any of
                // the "ready to groom" statuses: Designed (Designer
                // approved), Approved (operator non-visual fast-path),
                // Groomed (operator re-decompose).
                if (spec.Status is not (SpecStatus.Designed
                    or SpecStatus.AssetReady
                    or SpecStatus.Approved
                    or SpecStatus.Groomed))
                {
                    return Results.BadRequest(new
                    {
                        error = "spec_not_groomable",
                        detail = $"spec status is {spec.Status}; expected Designed | AssetReady | Approved | Groomed"
                    });
                }

                // Idempotency guard: grooming APPENDS stories/tasks.
                // A Groomed spec that already has stories was already
                // decomposed; re-grooming without an explicit force
                // piles up duplicates (observed 2026-07-22: ~27 repeat
                // grooms of one spec → 83 stories / 147 tasks / 28 PRs).
                // Intentional re-decomposition passes ?force=true.
                // The stories live in the SPEC'S PROJECT's store (the
                // groomer routes by spec.ProjectId), so the guard must
                // look there — not in the primary store.
                if (spec.Status == SpecStatus.Groomed
                    && issues is not null
                    && !string.Equals(force, "true", StringComparison.OrdinalIgnoreCase))
                {
                    var storyStore = projectContexts?.Find(spec.ProjectId)?.Issues ?? issues;
                    var existing = await storyStore.ListAsync(
                        new Forge.Core.IssueFilter { Type = "story" }, ct);
                    if (existing.Any(s => string.Equals(s.ParentIssueId, spec.Id, StringComparison.Ordinal)))
                    {
                        return Results.Conflict(new
                        {
                            error = "spec_already_groomed",
                            detail = "spec already has stories from a previous groom; re-run with ?force=true to re-decompose (appends new stories/tasks)"
                        });
                    }
                }

                // Fire-and-forget on a background task. The HTTP
                // request returns immediately so the UI can refresh
                // and watch the event stream. The manual run is
                // recorded in issue_groomer_run (P3.5) so the
                // dashboard's Groomer timeline can show it.
                var agent = groomerFactory.Create(projectId: spec.ProjectId);
                var runs = groomerRuns;
                _ = Task.Run(async () =>
                {
                    var run = runs is not null
                        ? await runs.StartAsync(id, GroomerTriggerKind.Manual, CancellationToken.None)
                        : null;
                    var startedAt = DateTime.UtcNow;
                    try
                    {
                        var result = await agent.GroomAsync(id);
                        if (runs is not null && run is not null)
                        {
                            await runs.FinishAsync(run.Id, GroomerRunStatus.Succeeded,
                                storiesProduced: result?.StoryIds.Count ?? 0,
                                tasksProduced: result?.TaskIds.Count ?? 0,
                                error: null,
                                duration: DateTime.UtcNow - startedAt,
                                ct: CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (runs is not null && run is not null)
                        {
                            await runs.FinishAsync(run.Id, GroomerRunStatus.Failed,
                                storiesProduced: 0, tasksProduced: 0,
                                error: $"{ex.GetType().Name}: {ex.Message}",
                                duration: DateTime.UtcNow - startedAt,
                                ct: CancellationToken.None);
                        }
                        logger.LogWarning(ex, "Background groom failed for spec {Id}", id);
                    }
                });
                return Results.Accepted($"/api/specs/{id}", new { status = "started" });
            });
        }

        // P6 Stage 4: action-state machine for the Specs matrix action
        // bar. Returns the buttons the operator can hit on this row
        // (Approve only on Draft, Start Grooming only on Approved |
        // Designed, etc.) so the UI doesn't ship its own copy of the
        // state machine.
        app.MapGet("/api/specs/{id}/actions", async (string id, string? project, CancellationToken ct) =>
        {
            var owned = ResolveOwned(project);
            if (owned is null) return Results.NotFound(new { error = "project not found", project });
            var spec = await owned.Value.Specs.GetAsync(id, ct);
            if (spec is null) return Results.NotFound();

            // Single source of truth: Core.SpecActions (the UI
            // consumes the same rules for its buttons).
            var canApprove = SpecActions.CanApprove(spec.Status);
            var canStartGrooming = SpecActions.CanStartGrooming(spec.Status);
            var canShip = SpecActions.CanShip(spec.Status);
            // Designer path: a Draft spec can be sent to the Designer
            // agent (Draft -> ReadyForDesign; the DesignerScheduler
            // picks it up automatically and populates the design
            // board).
            var canSendToDesign = SpecActions.CanSendToDesign(spec.Status);

            return Results.Json(new
            {
                canApprove,
                canStartGrooming,
                canShip,
                canSendToDesign,
                reason = canApprove ? "draft is ready for approval"
                    : canStartGrooming ? "designed/approved specs feed the groomer"
                    : canShip ? "groomer has decomposed into stories/tasks"
                    : $"status {spec.Status} has no available actions",
            });
        });
    }

    private static object ToTaskView(Forge.Core.IssueRecord t)
    {
        string? prNumber = null;
        string? branch = null;
        try
        {
            if (!string.IsNullOrEmpty(t.MetadataJson) && t.MetadataJson != "{}")
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t.MetadataJson);
                if (doc.RootElement.TryGetProperty("prNumber", out var pr)) prNumber = pr.ToString();
                if (doc.RootElement.TryGetProperty("branch", out var br)) branch = br.GetString();
            }
        }
        catch { /* metadata is advisory; never break the view */ }
        return new
        {
            id = t.Id,
            title = t.Title,
            status = t.Status.ToString(),
            priority = t.Priority,
            assignee = t.Assignee,
            prNumber,
            branch,
        };
    }

    private static object ToSpecView(SpecRecord s) => new
    {
        id = s.Id,
        projectId = s.ProjectId,
        title = s.Title,
        status = s.Status.ToString(),
        parentIssueId = s.ParentIssueId,
        parentSpecId = s.ParentSpecId,
        currentVersion = s.CurrentVersion,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
        body = s.Body,
        author = s.Author,
        // Server-authoritative action availability (Core.SpecActions)
        // — the UI must not ship its own copy of these rules.
        canApprove = SpecActions.CanApprove(s.Status),
        canStartGrooming = SpecActions.CanStartGrooming(s.Status),
    };

    private static object ToVersionView(SpecVersionRecord v) => new
    {
        specId = v.SpecId,
        version = v.Version,
        body = v.Body,
        author = v.Author,
        createdAt = v.CreatedAt
    };
}
