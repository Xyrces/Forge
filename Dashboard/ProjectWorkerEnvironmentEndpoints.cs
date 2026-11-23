using Forge.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Forge.Dashboard;

public static class ProjectWorkerEnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapProjectWorkerEnvironmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects/{id}/worker-environment");
        group.MapGet("", async (string id, [FromServices] IProjectWorkerEnvironmentStore store, CancellationToken ct) =>
            await RespondAsync(() => store.GetAsync(id, ct)));
        group.MapPut("", async (string id, SetWorkerEnvironmentDefinition body, [FromServices] IProjectWorkerEnvironmentStore store, CancellationToken ct) =>
            await RespondAsync(() => store.SetDefinitionAsync(id, body, ct)));
        // This is a trusted operator endpoint, never a worker tool. The build helper
        // verifies the repository revision, image and runtime before activation.
        group.MapPost("/qualify", async (string id, QualifyWorkerEnvironment body, [FromServices] IProjectWorkerEnvironmentStore store, CancellationToken ct) =>
            await RespondAsync(() => store.QualifyAsync(id, body, ct)));
        return endpoints;
    }

    private static async Task<IResult> RespondAsync(Func<Task<ProjectWorkerEnvironment?>> action)
    {
        try
        {
            var result = await action();
            return result is null ? Results.NotFound(new { error = "Project not found." }) : Results.Ok(result);
        }
        catch (ProjectSettingsConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
}
