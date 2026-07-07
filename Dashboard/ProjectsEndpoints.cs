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

        group.MapPatch("/{id}/slots/{role}", PatchSlotAsync)
             .WithName("PatchProjectSlot")
             .WithSummary("Adjust the in-process concurrency cap for a (project, role) pair.");

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
                p.Id, p.Name, p.Root, p.Roles,
                pending, inprogress, completed, failed,
                slots.Snapshot().Where(m => m.ProjectId == p.Id).ToList()));
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
            ctx.Options.Id, ctx.Options.Name, ctx.Options.Root, ctx.Options.Roles,
            pending, inprogress, completed, failed,
            slots.Snapshot().Where(m => m.ProjectId == id).ToList()));
    }

    private static IResult PatchSlotAsync(
        string id, string role, SlotTable slots,
        PatchSlotRequest? body)
    {
        if (body is null || body.Max < 1)
            return Results.BadRequest(new { error = "max must be >= 1" });
        slots.Configure(id, role, body.Max);
        return Results.Ok(new { projectId = id, role, max = body.Max });
    }

    public sealed record ProjectDto(
        string Id,
        string Name,
        string Root,
        Dictionary<string, int> Roles,
        int Pending,
        int InProgress,
        int Completed,
        int Failed,
        IReadOnlyList<SlotTable.SlotMeter> Slots);

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
