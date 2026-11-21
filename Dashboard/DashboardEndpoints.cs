using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Dashboard;

/// <summary>
/// P1 endpoints: agent/skill/sprint CRUD, agent messages, issue CRUD.
/// Mounted into the DashboardHost's WebApplication via
/// <see cref="MapP1Endpoints"/>.
/// </summary>
public static class DashboardEndpoints
{
    public static void MapP1Endpoints(
        WebApplication app,
        IIssueStore issues,
        IAgentStore agents,
        ISkillStore skills,
        ISprintStore sprints,
        AgentMessageBus messageBus,
        ILogger logger,
        Projects.ProjectContextFactory? projectContexts = null,
        StageGates? gates = null, Forge.Configuration.SprintPlanningOptions? planning = null)
    {
        // Multi-project store resolution: an explicit but UNKNOWN
        // projectId 404s â€” silently falling back to the primary store
        // misroutes reads AND writes onto the primary project's
        // same-numbered rows (ids are per-project sequences). Absent
        // projectId = primary (legacy).
        IIssueStore? ResolveIssues(string? projectId, out IResult? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(projectId) || projectContexts is null) return issues;
            var pctx = projectContexts.Find(projectId);
            if (pctx is not null) return pctx.Issues;
            error = Results.NotFound(new { error = "project not found", projectId });
            return null;
        }
        ISprintStore? ResolveSprints(string? projectId, out IResult? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(projectId) || projectContexts is null) return sprints;
            var pctx = projectContexts.Find(projectId);
            if (pctx is not null) return pctx.Sprints;
            error = Results.NotFound(new { error = "project not found", projectId });
            return null;
        }

        SprintActivationValidator Activation(IIssueStore owner, ISprintStore sprintStore, string? projectId)
        {
            if (owner is not IssueStore concrete)
                throw new InvalidOperationException("Sprint activation requires the owning project's stores.");
            return new SprintActivationValidator(owner, sprintStore, new SpecStore(concrete),
                projectId ?? concrete.ProjectId, gates, planning, projectContexts?.Find(projectId ?? concrete.ProjectId)?.Options.Roles);
        }

        // ---- Issues (POST + PATCH) ----
        app.MapPost("/api/state/issues", async (HttpContext ctx, string? projectId) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewIssue>(ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.Type) || string.IsNullOrWhiteSpace(spec.Title))
                return Results.BadRequest(new { error = "type and title required" });
            // Multi-project: ?projectId= writes to that project's
            // store; absent falls back to the injected primary store.
            var store = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var pctx = projectContexts.Find(projectId);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                store = pctx.Issues;
            }
            var created = await store.CreateAsync(spec, ctx.RequestAborted);
            return Results.Json(ToIssueView(created), DashboardJson.Options, statusCode: 201);
        });

app.MapPatch("/api/state/issues/{id}", async (string id, HttpContext ctx, string? projectId) =>
        {
            var patch = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (patch is null) return Results.BadRequest(new { error = "empty body" });
            // Multi-project: operator task mutations route to the
            // owning project's store (the default is the primary).
            var store = ResolveIssues(projectId, out var issueErr);
            if (issueErr is not null) return issueErr;
            var existing = await store!.GetAsync(id, ctx.RequestAborted);
            if (existing is null) return Results.NotFound();
            if (patch.TryGetValue("status", out var st) && Enum.TryParse<IssueStatus>(st?.ToString() ?? "", out var toStatus))
            {
                var updated = await store.TransitionAsync(id, toStatus,
                    patch.TryGetValue("error", out var e) ? e?.ToString() : null,
                    ct: ctx.RequestAborted);
                return Results.Json(ToIssueView(updated), DashboardJson.Options);
            }
            return Results.BadRequest(new { error = "unsupported patch" });
        });

        // ---- Issue dependency graph (Phase 2 of docs/embedded-issues.md) ----
        // Ids are per-project sequences â€” dep reads/writes route to
        // the owning project's store (default is the primary).
        app.MapGet("/api/state/issues/{id}/deps", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = ResolveIssues(projectId, out var err);
            if (err is not null) return err;
            var existing = await store!.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            var deps = await store.DependenciesAsync(id, ct);
            var blocked = await store.IsBlockedAsync(id, ct);
            return Results.Json(new
            {
                issueId = id,
                blocked,
                edges = deps.Select(d => new
                {
                    blockerId = d.BlockerId,
                    blockedId = d.BlockedId,
                    kind = d.Kind.ToString().ToLowerInvariant(),
                    createdAt = d.CreatedAt,
                }).ToArray(),
            }, DashboardJson.Options);
        });

        app.MapPost("/api/state/issues/{id}/deps", async (string id, HttpContext ctx, string? projectId) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (body is null) return Results.BadRequest(new { error = "empty body" });
            if (!body.TryGetValue("blockerId", out var blockerObj) || blockerObj is null)
                return Results.BadRequest(new { error = "blockerId required" });
            if (!body.TryGetValue("kind", out var kindObj) || kindObj is null)
                return Results.BadRequest(new { error = "kind required (blocks | related | duplicates)" });
            var blockerId = blockerObj.ToString() ?? "";
            var kindStr = kindObj.ToString() ?? "";
            if (!IssueDepKindExtensions.TryParseDb(kindStr, out var kind))
                return Results.BadRequest(new { error = $"unknown kind '{kindStr}'" });

            var store = ResolveIssues(projectId, out var err);
            if (err is not null) return err;
            try
            {
                var edge = await store!.AddDependencyAsync(blockerId, id, kind, ctx.RequestAborted);
                return Results.Json(new
                {
                    blockerId = edge.BlockerId,
                    blockedId = edge.BlockedId,
                    kind = edge.Kind.ToString().ToLowerInvariant(),
                    createdAt = edge.CreatedAt,
                }, DashboardJson.Options, statusCode: 201);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/state/issues/{id}/deps/{blockerId}/{kind}", async (string id, string blockerId, string kind, string? projectId, CancellationToken ct) =>
        {
            if (!IssueDepKindExtensions.TryParseDb(kind, out var kindEnum))
                return Results.BadRequest(new { error = $"unknown kind '{kind}'" });
            var store = ResolveIssues(projectId, out var err);
            if (err is not null) return err;
            var removed = await store!.RemoveDependencyAsync(blockerId, id, kindEnum, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // ---- Agents ----
        app.MapGet("/api/agents/db", async (CancellationToken ct) =>
        {
            var list = await agents.ListAsync(ct);
            return Results.Json(list.Select(ToAgentView).ToArray(), DashboardJson.Options);
        });

        app.MapPost("/api/agents/db", async (HttpContext ctx) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewAgent>(ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.AgentName) || string.IsNullOrWhiteSpace(spec.DisplayName))
                return Results.BadRequest(new { error = "agentName and displayName required" });
            var created = await agents.CreateAsync(spec, ctx.RequestAborted);
            return Results.Json(ToAgentView(created), DashboardJson.Options, statusCode: 201);
        });

        app.MapPatch("/api/agents/db/{id}", async (string id, HttpContext ctx) =>
        {
            var patch = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (patch is null) return Results.BadRequest();
            var updated = await agents.UpdateAsync(id, patch, ctx.RequestAborted);
            return Results.Json(ToAgentView(updated), DashboardJson.Options);
        });

        app.MapDelete("/api/agents/db/{id}", async (string id, CancellationToken ct) =>
        {
            await agents.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // ---- Skills ----
        app.MapGet("/api/skills", async (string? role, string? global, string? projectId, CancellationToken ct) =>
        {
            var list = await skills.ListByRoleAsync(role, global == "true", ct);
            if (!string.IsNullOrWhiteSpace(projectId))
                list = list.Where(s => s.ProjectId is null || string.Equals(s.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Json(list.Select(ToSkillView).ToArray(), DashboardJson.Options);
        });

        app.MapPost("/api/skills", async (HttpContext ctx) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewSkill>(ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.Name)) return Results.BadRequest();
            // UI-created skills are always Forge-owned â€” the repo
            // source is reserved for the startup importer.
            var created = await skills.CreateAsync(spec with { Source = Core.SkillSources.Forge }, ctx.RequestAborted);
            return Results.Json(ToSkillView(created), DashboardJson.Options, statusCode: 201);
        });

        app.MapPatch("/api/skills/{id}", async (string id, HttpContext ctx) =>
        {
            var patch = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (patch is null) return Results.BadRequest();
            try
            {
                var updated = await skills.UpdateAsync(id, patch, ctx.RequestAborted);
                return Results.Json(ToSkillView(updated), DashboardJson.Options);
            }
            catch (Core.RepoOwnedSkillException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/skills/{id}", async (string id, CancellationToken ct) =>
        {
            try
            {
                await skills.DeleteAsync(id, ct);
                return Results.NoContent();
            }
            catch (Core.RepoOwnedSkillException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // ---- Sprints ----
        app.MapGet("/api/sprints", async (string? active, string? projectId, CancellationToken ct) =>
        {
            // Multi-project: sprint reads route to the owning project's
            // store (default is the primary), same as issue reads.
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            if (active == "true")
            {
                var s = await store!.GetActiveAsync(ct);
                return Results.Json(s is null ? Array.Empty<object>() : new[] { ToSprintView(s) }, DashboardJson.Options);
            }
            var list = await store!.ListAsync(activeOnly: false, ct);
            return Results.Json(list.Select(ToSprintView).ToArray(), DashboardJson.Options);
        });

        app.MapPost("/api/sprints", async (HttpContext ctx, string? projectId) =>
        {
            var spec = await JsonSerializer.DeserializeAsync<NewSprint>(ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (spec is null || string.IsNullOrWhiteSpace(spec.Name) || string.IsNullOrWhiteSpace(spec.Goal))
                return Results.BadRequest(new { error = "name and goal required" });
            // Sprint writes route to the owning project's store
            // (default is the primary) â€” same rule as sprint reads.
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            try
            {
                // The legacy create contract carries no members. Active creation must
                // therefore fail the shared nonempty-selection rule; proposals/assembly
                // create active sprints with validated membership atomically.
                if (spec.Status == SprintStatus.Active)
                {
                    var owner = ResolveIssues(projectId, out _)!;
                    await Activation(owner, store!, projectId).ValidateAsync([], ctx.RequestAborted);
                }
                var created = await store!.CreateAsync(spec, ctx.RequestAborted);
                return Results.Json(ToSprintView(created), DashboardJson.Options, statusCode: 201);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapPatch("/api/sprints/{id}", async (string id, HttpContext ctx, string? projectId) =>
        {
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            var patch = await JsonSerializer.DeserializeAsync<Dictionary<string, object?>>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (patch is null) return Results.BadRequest();
            try
            {
                if (patch.TryGetValue("status", out var st)
                    && Enum.TryParse<SprintStatus>(st?.ToString(), ignoreCase: true, out var requestedStatus)
                    && requestedStatus == SprintStatus.Active)
                {
                    var existing = await store!.GetAsync(id, ctx.RequestAborted);
                    if (existing is null) return Results.NotFound();
                    if (existing.Status == SprintStatus.Active)
                        return Results.Json(ToSprintView(existing), DashboardJson.Options);
                    var owner = ResolveIssues(projectId, out _)!;
                    var members = await store.GetIssueIdsAsync(id, ctx.RequestAborted);
                    var taskIds = new List<string>();
                    foreach (var member in members)
                    {
                        var issue = await owner.GetAsync(member, ctx.RequestAborted);
                        if (issue is null) return Results.Conflict(new { error = "Sprint member no longer exists." });
                        if (!AgentTaskTypes.IsContainer(issue.Type)) taskIds.Add(member);
                    }
                    var snapshot = await Activation(owner, store, projectId).ValidateAsync(taskIds, ctx.RequestAborted);
                    var activated = await store.ActivateWithIssuesAsync(id, snapshot, members, ctx.RequestAborted);
                    return Results.Json(ToSprintView(activated), DashboardJson.Options);
                }
                if (patch.ContainsKey("status") && await store!.GetAsync(id, ctx.RequestAborted) is { Status: SprintStatus.Active })
                    return Results.Conflict(new { error = "Active sprint completion requires verified completion evidence; use an explicit stop action to interrupt." });
                if (patch.ContainsKey("goal") && await store!.GetAsync(id, ctx.RequestAborted) is { Status: SprintStatus.Active })
                    return Results.Conflict(new { error = "An active approved outcome cannot be replaced by a manual goal." });
                var updated = await store!.UpdateAsync(id, patch, ctx.RequestAborted);
                return Results.Json(ToSprintView(updated), DashboardJson.Options);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapPost("/api/sprints/{id}/issues", async (string id, HttpContext ctx, string? projectId) =>
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                ctx.Request.Body, DashboardJson.Options, ctx.RequestAborted);
            if (body is null || !body.TryGetValue("issueId", out var issueId) || string.IsNullOrEmpty(issueId))
                return Results.BadRequest(new { error = "issueId required" });
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            var sprint = await store!.GetAsync(id, ctx.RequestAborted);
            if (sprint is null) return Results.NotFound();
            try
            {
                if (sprint.Status == SprintStatus.Active)
                {
                    if ((await store.GetIssueIdsAsync(id, ctx.RequestAborted)).Contains(issueId))
                        return Results.NoContent();
                    var owner = ResolveIssues(projectId, out _)!;
                    var snapshot = await Activation(owner, store, projectId).ValidateAsync([issueId], ctx.RequestAborted, activating: false);
                    await store.AddValidatedIssueAsync(id, snapshot, ctx.RequestAborted);
                }
                else await store.AddInactiveIssueAsync(id, issueId, ctx.RequestAborted);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapDelete("/api/sprints/{id}/issues/{issueId}", async (string id, string issueId, string? projectId, CancellationToken ct) =>
        {
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            if (await store!.GetAsync(id, ct) is { Status: SprintStatus.Active })
                return Results.Conflict(new { error = "Cannot remove required outcome members from an active sprint; interrupt it explicitly before scope repair." });
            try { await store.RemoveIssueAsync(id, issueId, ct); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        app.MapGet("/api/sprints/{id}/issues", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            var ids = await store!.GetIssueIdsAsync(id, ct);
            return Results.Json(ids, DashboardJson.Options);
        });

        // Inter-sprint build state (operator request 2026-08-06) â€”
        // separate mapper so tests can mount it in isolation.
        SprintBuildEndpoints.Map(app, issues, projectContexts);


        app.MapGet("/api/sprints/{id}/followup-drafts", async (string id, string? projectId, CancellationToken ct) =>
        {
            var issueStore = issues;
            if (projectId is not null && projectContexts is not null)
            {
                var pctx = projectContexts.Find(projectId);
                if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                issueStore = pctx.Issues;
            }
            var drafts = await new Forge.Core.FollowUpDraftStore((Forge.Core.IssueStore)issueStore)
                .ListOpenForSprintAsync(id, ct);
            return Results.Json(drafts.Select(d => new
            {
                id = d.Id,
                title = d.Title,
                description = d.Description,
                priority = d.Priority,
                sourceIssueId = d.SourceIssueId,
                sourceRole = d.SourceRole,
                createdAt = d.CreatedAt,
            }), DashboardJson.Options);
        });

        app.MapDelete("/api/sprints/{id}", async (string id, string? projectId, CancellationToken ct) =>
        {
            var store = ResolveSprints(projectId, out var err);
            if (err is not null) return err;
            if (await store!.GetAsync(id, ct) is { Status: SprintStatus.Active })
                return Results.Conflict(new { error = "An active sprint cannot be deleted to bypass completion or explicit interruption." });
            try { await store.DeleteAsync(id, ct); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // ---- Agent messages ----
        app.MapPost("/api/agents/{agentName}/messages", (string agentName, HttpContext ctx) =>
        {
            return ReadMessageBodyAsync(ctx, message =>
            {
                messageBus.Enqueue(agentName, message);
                logger.LogInformation("Queued message for agent {Agent} (pending count: {N})",
                    agentName, messageBus.Count(agentName));
                return Results.Accepted();
            });
        });

        app.MapGet("/api/agents/{agentName}/messages", (string agentName) =>
        {
            return Results.Json(new { agent = agentName, pending = messageBus.Count(agentName) },
                DashboardJson.Options);
        });
    }

    private static async Task<IResult> ReadMessageBodyAsync(HttpContext ctx, Func<string, IResult> handler)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (!doc.RootElement.TryGetProperty("text", out var textEl))
                return Results.BadRequest(new { error = "text required" });
            return handler(textEl.GetString() ?? "");
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static object ToIssueView(IssueRecord t) => new
    {
        id = t.Id,
        type = t.Type,
        title = t.Title,
        description = t.Description,
        status = t.Status.ToString(),
        priority = t.Priority,
        assignee = t.Assignee,
        createdAt = t.CreatedAt,
        updatedAt = t.UpdatedAt,
        closedAt = t.ClosedAt,
        parameters = ParseMetadata(t.MetadataJson)
    };

    private static Dictionary<string, object> ParseMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, object>>(json, DashboardJson.Options) ?? new(); }
        catch { return new(); }
    }

    private static object ToAgentView(AgentRecord a) => new
    {
        id = a.Id,
        agentName = a.AgentName,
        displayName = a.DisplayName,
        scope = a.Scope,
        description = a.Description,
        enabled = a.Enabled,
        configJson = a.ConfigJson,
        createdAt = a.CreatedAt,
        updatedAt = a.UpdatedAt
    };

    private static object ToSkillView(SkillRecord s) => new
    {
        id = s.Id,
        name = s.Name,
        description = s.Description,
        body = s.Body,
        agentId = s.AgentId,
        roles = s.Roles,
        enabled = s.Enabled,
        projectId = s.ProjectId,
        source = s.Source,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt
    };

    private static object ToSprintView(SprintRecord s) => new
    {
        id = s.Id,
        name = s.Name,
        goal = s.Goal,
        startDate = s.StartDate,
        endDate = s.EndDate,
        status = s.Status.ToString(),
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt
    };
}
