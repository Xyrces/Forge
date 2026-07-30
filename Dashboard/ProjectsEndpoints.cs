using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator.Slots;
using Forge.Projects;

namespace Forge.Dashboard;

public static class ProjectsEndpoints
{
    public static IEndpointRouteBuilder MapProjectsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects");

        group.MapGet("/", ListProjectsAsync)
             .WithName("ListProjects")
             .WithSummary("List registered projects + per-project status counters + slot meters.");

        group.MapGet("/{id}", GetProjectAsync)
             .WithName("GetProject")
             .WithSummary("Single-project detail (counters + role caps).");

        group.MapPost("/", AddProjectAsync)
             .WithName("AddProject")
             .WithSummary("Register a new project + clone its repo on first boot. Idempotent.");

        group.MapDelete("/{id}", RemoveProjectAsync)
             .WithName("RemoveProject")
             .WithSummary("Remove a project from the registry. Does NOT delete the local clone or worktrees.");

        group.MapPost("/{id}/sync", SyncProjectAsync)
             .WithName("SyncProject")
             .WithSummary("git pull --ff-only origin <defaultBranch>. Updates local_path on success.");

        group.MapPatch("/{id}/slots/{role}", PatchSlotAsync)
             .WithName("PatchProjectSlot")
             .WithSummary("Adjust the in-process concurrency cap for a (project, role) pair.");

        group.MapPut("/{id}/roles", PutRolesAsync)
             .WithName("PutProjectRoles")
             .WithSummary("Replace the project's persisted role-cap overrides (role -> max) and apply them to the live slot table.");
        endpoints.MapGet("/api/board", BoardAsync)
                 .WithName("CrossProjectBoard")
                 .WithSummary("Cross-project kanban feed: each row tagged with project_id.");

        return endpoints;
    }

    private static async Task<IResult> ListProjectsAsync(
        ProjectContextFactory factory,
        SlotTable slots,
        CancellationToken ct)
    {
        var rows = new List<ProjectDto>();
        foreach (var p in factory.KnownProjects)
        {
            var ctx = factory.Find(p.Id);
            var pending = ctx is null ? 0 : await ctx.CountByStatusAsync(IssueStatus.Pending, ct);
            var inprogress = ctx is null ? 0 : await ctx.CountByStatusAsync(IssueStatus.InProgress, ct);
            var completed = ctx is null ? 0 : await ctx.CountByStatusAsync(IssueStatus.Completed, ct);
            var failed = ctx is null ? 0 : await ctx.CountByStatusAsync(IssueStatus.Failed, ct);
            rows.Add(new ProjectDto(
                p.Id, p.Name, p.RepoUrl, p.DefaultBranch, p.Root, p.Roles,
                pending, inprogress, completed, failed,
                slots.Snapshot().Where(m => m.ProjectId == p.Id).ToList(),
                DefaultRoleCaps: new Dictionary<string, int>(Configuration.DefaultProjectRoles.Default, StringComparer.OrdinalIgnoreCase),
                Territories: p.Territories));
        }
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetProjectAsync(
        string id, ProjectContextFactory factory, SlotTable slots, CancellationToken ct)
    {
        var ctx = factory.Find(id);
        if (ctx is null) return Results.NotFound(new { error = "project not found", id });
        var pending = await ctx.CountByStatusAsync(IssueStatus.Pending, ct);
        var inprogress = await ctx.CountByStatusAsync(IssueStatus.InProgress, ct);
        var completed = await ctx.CountByStatusAsync(IssueStatus.Completed, ct);
        var failed = await ctx.CountByStatusAsync(IssueStatus.Failed, ct);
        return Results.Ok(new ProjectDto(
            ctx.Options.Id, ctx.Options.Name, ctx.Options.RepoUrl, ctx.Options.DefaultBranch, ctx.Options.Root, ctx.Options.Roles,
            pending, inprogress, completed, failed,
            slots.Snapshot().Where(m => m.ProjectId == id).ToList(),
            Territories: ctx.Options.Territories));
    }

    private static async Task<IResult> AddProjectAsync(
        AddProjectRequest? body,
        IProjectStore store,
        ProjectCloner cloner,
        Configuration.GitHubOptions github,
        ISecretStore secrets,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Id) || string.IsNullOrWhiteSpace(body.RepoUrl))
            return Results.BadRequest(new { error = "id and repoUrl are required" });
        if (!IsValidId(body.Id))
            return Results.BadRequest(new { error = "id must match [a-z0-9][a-z0-9_-]* (lowercase, 1-32 chars)" });

        var logger = loggerFactory.CreateLogger("Projects.Add");

        // Register the row first (secret has an FK to it). The branch
        // may be a placeholder here — detection below re-upserts.
        var record = await store.UpsertAsync(new NewProject(
            Id: body.Id,
            Name: string.IsNullOrWhiteSpace(body.Name) ? body.Id : body.Name,
            RepoUrl: body.RepoUrl,
            DefaultBranch: string.IsNullOrWhiteSpace(body.DefaultBranch) ? "main" : body.DefaultBranch), ct);

        // A token entered in the Add Project form becomes the
        // project's github_token secret — stored before detection and
        // the inline clone so both already resolve it.
        if (!string.IsNullOrEmpty(body.GitToken))
        {
            await secrets.UpsertAsync(new NewSecret(
                body.Id, SecretKinds.GitHubToken,
                System.Text.Encoding.UTF8.GetBytes(body.GitToken)), ct);
        }

        // Default-branch resolution, in order: (1) git token, (2) the
        // git repo's advertised HEAD, (3) the clone's own pull info
        // (post-clone, below), (4) user override — which always wins.
        var effectiveGitHub = await GitHubTokenResolver.ResolveAsync(body.Id, github, secrets, ct);
        string? detected = null;
        if (string.IsNullOrWhiteSpace(body.DefaultBranch))
        {
            detected = await cloner.DetectDefaultBranchAsync(
                new ProjectOptions { Id = body.Id, Name = body.Name ?? body.Id, RepoUrl = body.RepoUrl },
                effectiveGitHub, ct);
            if (!string.IsNullOrEmpty(detected) &&
                !string.Equals(detected, record.DefaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                record = await store.UpsertAsync(
                    new NewProject(record.Id, record.Name, record.RepoUrl, detected), ct);
            }
        }

        // Attempt the clone inline. Failure doesn't roll back the
        // registry row — the operator can retry via sync, or the
        // next startup will attempt it again.
        ProjectCloneResult? clone = null;
        try
        {
            clone = await cloner.CloneAsync(new ProjectOptions
            {
                Id = record.Id,
                Name = record.Name,
                RepoUrl = record.RepoUrl,
                DefaultBranch = record.DefaultBranch,
            }, effectiveGitHub, ct);
            await store.UpdateLocalPathAsync(record.Id, clone.LocalPath, ct);

            // (3) pull info: the clone's origin/HEAD is ground truth.
            // Correct the stored branch unless the user overrode it.
            if (string.IsNullOrWhiteSpace(body.DefaultBranch))
            {
                var cloneBranch = await cloner.ReadCloneDefaultBranchAsync(clone.LocalPath);
                if (!string.IsNullOrEmpty(cloneBranch) &&
                    !string.Equals(cloneBranch, record.DefaultBranch, StringComparison.OrdinalIgnoreCase))
                {
                    record = await store.UpsertAsync(
                        new NewProject(record.Id, record.Name, record.RepoUrl, cloneBranch), ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Project '{Id}' added but initial clone failed; will retry on next sync/startup", record.Id);
            await store.UpdateSyncStatusAsync(record.Id, DateTime.UtcNow, ex.Message, ct);
            return Results.Json(new
            {
                project = record,
                clone = (ProjectCloneResult?)null,
                warning = $"registered, but clone failed: {ex.Message}",
            }, statusCode: 202);
        }

        await store.UpdateSyncStatusAsync(record.Id, DateTime.UtcNow, null, ct);
        return Results.Ok(new { project = record, clone });
    }

    private static async Task<IResult> RemoveProjectAsync(
        string id, IProjectStore store, CancellationToken ct)
    {
        var removed = await store.DeleteAsync(id, ct);
        return removed ? Results.NoContent() : Results.NotFound(new { error = "project not found", id });
    }

    private static async Task<IResult> SyncProjectAsync(
        string id, IProjectStore store, ProjectCloner cloner,
        Configuration.GitHubOptions github,
        ISecretStore secrets,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Projects.Sync");
        var record = await store.GetAsync(id, ct);
        if (record is null) return Results.NotFound(new { error = "project not found", id });

        var effectiveGitHub = await GitHubTokenResolver.ResolveAsync(record.Id, github, secrets, ct);
        var sync = await cloner.SyncAsync(new ProjectOptions
        {
            Id = record.Id,
            Name = record.Name,
            RepoUrl = record.RepoUrl,
            DefaultBranch = record.DefaultBranch,
        }, effectiveGitHub, ct);
        var ok = sync.Ok;
        await store.UpdateSyncStatusAsync(id, DateTime.UtcNow, ok ? null : "git pull failed (see journalctl)", ct);
        if (ok)
        {
            await store.UpdateLocalPathAsync(id, cloner.LocalPathFor(id), ct);
            // Reconciliation may have discovered the remote's real
            // default branch — persist it so later clones/syncs/
            // dispatches use it too.
            if (!string.Equals(sync.Branch, record.DefaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                await store.UpsertAsync(new NewProject(record.Id, record.Name, record.RepoUrl, sync.Branch), ct);
            }
        }
        return ok ? Results.Ok(new { id, syncedAt = DateTime.UtcNow })
                  : Results.Json(new { error = "git pull failed; check journalctl" }, statusCode: 502);
    }

    private static bool IsValidId(string id) =>
        id.Length >= 1 && id.Length <= 32 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-') &&
        char.IsAsciiLetterOrDigit(id[0]);

    private static IResult PatchSlotAsync(
        string id, string role, SlotTable slots,
        PatchSlotRequest? body)
    {
        if (body is null || body.Max < 1)
            return Results.BadRequest(new { error = "max must be >= 1" });
        slots.Configure(id, role, body.Max);
        return Results.Ok(new { projectId = id, role, max = body.Max });
    }

    private static async Task<IResult> PutRolesAsync(
        string id,
        PutRolesRequest? body,
        IProjectStore store,
        SlotTable slots,
        CancellationToken ct)
    {
        if (body?.Roles is null && body?.Territory is null && body?.Verify is null)
            return Results.BadRequest(new { error = "roles and/or territory and/or verify required, e.g. { \"roles\": { \"coredev\": 2 }, \"verify\": [\"dotnet build -c Release\", \"dotnet test -c Release\"] }" });

        if (body.Roles is not null)
        {
            // Validate: role keys are non-empty short tokens; caps 1..32.
            foreach (var (role, max) in body.Roles)
            {
                if (string.IsNullOrWhiteSpace(role) || role.Length > 32)
                    return Results.BadRequest(new { error = $"invalid role key '{role}'" });
                if (max < 1 || max > 32)
                    return Results.BadRequest(new { error = $"role '{role}': max must be 1..32 (got {max})" });
            }

            var roles = new Dictionary<string, int>(body.Roles, StringComparer.OrdinalIgnoreCase);
            var updated = await store.UpdateRolesAsync(id, roles, ct);
            if (!updated) return Results.NotFound(new { error = "project not found", id });

            // Apply live: re-seed every known role (defaults ∪ overrides)
            // so removing an override also resets the slot to its default.
            var allRoles = new HashSet<string>(Configuration.DefaultProjectRoles.Default.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var r in roles.Keys) allRoles.Add(r);
            foreach (var r in allRoles)
            {
                slots.Configure(id, r, Configuration.DefaultProjectRoles.MaxFor(roles, r));
            }
        }

        if (body.Territory is not null)
        {
            // Validate: role keys short tokens; prefixes repo-relative,
            // no absolute paths / traversal.
            foreach (var (role, t) in body.Territory)
            {
                if (string.IsNullOrWhiteSpace(role) || role.Length > 32)
                    return Results.BadRequest(new { error = $"invalid territory role key '{role}'" });
                foreach (var p in t.Prefixes)
                {
                    if (string.IsNullOrWhiteSpace(p) || p.StartsWith('/') || p.Contains(".."))
                        return Results.BadRequest(new { error = $"role '{role}': invalid territory prefix '{p}'" });
                }
            }
            var territories = body.Territory.ToDictionary(
                kv => kv.Key,
                kv => new Core.RoleTerritory(kv.Value.Prefixes, kv.Value.RootFiles),
                StringComparer.OrdinalIgnoreCase);
            var updated = await store.UpdateTerritoriesAsync(id, territories, ct);
            if (!updated) return Results.NotFound(new { error = "project not found", id });
        }

        if (body.Verify is not null)
        {
            // Pre-push verification commands: non-empty, bounded length,
            // no absolute paths outside the worktree assumptions.
            if (body.Verify.Count > 16 || body.Verify.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 400))
                return Results.BadRequest(new { error = "verify: up to 16 non-empty commands (<=400 chars each)" });
            var updated = await store.UpdateVerifyCommandsAsync(id, body.Verify, ct);
            if (!updated) return Results.NotFound(new { error = "project not found", id });
        }

        var project = await store.GetAsync(id, ct);
        return Results.Ok(new { projectId = id, roles = project?.Roles, territory = project?.Territories, verify = project?.VerifyCommands });
    }

    public sealed record PutRolesRequest(
        Dictionary<string, int>? Roles,
        Dictionary<string, PutTerritoryEntry>? Territory = null,
        List<string>? Verify = null);

    public sealed record PutTerritoryEntry(List<string> Prefixes, bool RootFiles = false);

    public sealed record ProjectDto(
        string Id,
        string Name,
        string RepoUrl,
        string DefaultBranch,
        string Root,
        Dictionary<string, int> Roles,
        int Pending,
        int InProgress,
        int Completed,
        int Failed,
        IReadOnlyList<SlotTable.SlotMeter> Slots,
        IReadOnlyDictionary<string, int>? DefaultRoleCaps = null,
        IReadOnlyDictionary<string, Core.RoleTerritory>? Territories = null);

    public sealed record AddProjectRequest(
        string Id,
        string Name,
        string RepoUrl,
        string? DefaultBranch,
        string? GitToken = null);

    public sealed record PatchSlotRequest(int Max);

    public sealed record BoardIssueRow(
        string ProjectId,
        string Id,
        string Title,
        string Status,
        int Priority,
        string? Type,
        DateTime UpdatedAt,
        DateTime CreatedAt);

    private static async Task<IResult> BoardAsync(
        ProjectContextFactory factory, CancellationToken ct)
    {
        var rows = new List<BoardIssueRow>(64);
        foreach (var p in factory.KnownProjects)
        {
            var ctx = factory.Find(p.Id);
            if (ctx is null) continue;
            var all = await ctx.Issues.ListAsync(new IssueFilter(), ct);
            foreach (var i in all)
            {
                rows.Add(new BoardIssueRow(
                    p.Id, i.Id, i.Title, i.Status.ToString(), i.Priority,
                    i.Type, i.UpdatedAt, i.CreatedAt));
            }
        }
        return Results.Ok(new { issues = rows, totalProjects = factory.KnownProjects.Count });
    }
}
