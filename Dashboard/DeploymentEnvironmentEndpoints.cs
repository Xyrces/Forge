using Forge.Deploy;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Forge.Dashboard;

internal static class DeploymentEnvironmentEndpoints
{
    internal sealed record VerifiedInstall(string Environment, string Operation, string SourceSha);

    internal static void MapInstallation(IEndpointRouteBuilder endpoints, IRoutineReleaseSource releases)
    {
        endpoints.MapPost("/api/projects/{id}/environments/verified-installation", async (string id, VerifiedInstall request,
            ProjectContextFactory projects, DeploymentExecutorFactory executors, HttpContext http, CancellationToken ct) =>
        {
            if (http.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string actor) return Results.Unauthorized();
            var ctx = projects.Find(id);
            if (ctx is null) return Results.NotFound();
            if (request.Operation is null || !System.Text.RegularExpressions.Regex.IsMatch(request.Operation, "^[a-z0-9][-a-z0-9]{0,47}[a-z0-9]$"))
                return Results.BadRequest(new { error = "Invalid installer operation name." });
            try
            {
                var project = await ctx.Deployments.GetEffectiveProjectAsync(ctx.Options, ct);
                var target = Configuration.DeploymentEnvironmentResolver.Capture(project, request.Environment);
                var receipt = await releases.FindReceiptAsync(project, request.SourceSha, ct);
                if (receipt is null) return Results.Conflict(new { error = "Awaiting successful CI and image verification for this exact source." });
                var candidate = new DeploymentCandidate(request.Operation, id, request.SourceSha, "Verified installer release", DeploymentStatus.Deployed,
                    DateTime.UtcNow, actor, null, null, null, null, null, ReceiptJson: System.Text.Json.JsonSerializer.Serialize(receipt, DeploymentStore.EnvironmentJson),
                    EnvironmentName: target.Name, TargetJson: System.Text.Json.JsonSerializer.Serialize(target, DeploymentStore.EnvironmentJson));
                var executor = (IIndependentDeploymentExecutor)executors.Create(project)!;
                // With no admission callback this operation can only read the target's existing release journal.
                var result = await executor.ReadProgressAsync(project, candidate, ct);
                if (!result.Success || result.StillInProgress || result.AdmissionHeld) return Results.Conflict(new { error = result.Log });
                return Results.Ok(await ctx.Deployments.RecordVerifiedInstallationAsync(candidate, result.Log, ct));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    internal static void Map(IEndpointRouteBuilder endpoints, RouteGroupBuilder deployments, Action? wakeDelivery)
    {
        var settings = endpoints.MapGroup("/api/projects/{id}/environments");
        settings.MapGet("", async (string id, ProjectContextFactory projects, CancellationToken ct) =>
        {
            var project = projects.Find(id);
            return project is null ? Results.NotFound() : Results.Ok(await project.Deployments.GetEnvironmentPolicyAsync(project.Options, ct));
        });
        settings.MapPut("", async (string id, ProjectEnvironmentPolicy policy, ProjectContextFactory projects, HttpContext http, CancellationToken ct) =>
        {
            if (http.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string actor) return Results.Unauthorized();
            var project = projects.Find(id);
            if (project is null) return Results.NotFound();
            try { return Results.Ok(await project.Deployments.SaveEnvironmentPolicyAsync(project.Options, policy, actor, ct)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        deployments.MapPost("/{id}/retry", async (string id, string projectId, RetryReleaseRequest request,
            ProjectContextFactory projects, DeploymentExecutorFactory executors, HttpContext http, CancellationToken ct) =>
        {
            if (http.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string actor) return Results.Unauthorized();
            var project = projects.Find(projectId);
            if (project is null) return Results.NotFound();
            var candidate = await project.Deployments.GetAsync(id, ct);
            if (candidate is null || candidate.ProjectId != projectId) return Results.NotFound();
            if (candidate.CompletionId is null || candidate.EnvironmentName is null
                || candidate.Status is not (DeploymentStatus.DeployFailed or DeploymentStatus.Rejected))
                return Results.Conflict(new { error = "Only a failed or rejected named sprint deployment can be retried." });
            try
            {
                var executor = executors.Create(project.Options) as KubernetesDeploymentExecutor
                    ?? throw new InvalidOperationException("Retry requires the configured Kubernetes release executor.");
                if (!await executor.IsRetrySafeAsync(project.Options, candidate, ct))
                    return Results.Conflict(new { error = "The previous rollout has not confirmed safe termination or rollback. Resolve its deployment operation before retrying." });
                var retry = await project.Deployments.RetryRoutineAsync(project.Options, candidate, request, actor, ct);
                wakeDelivery?.Invoke();
                return Results.Ok(retry);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        deployments.MapPost("/{id}/promote", async (string id, string projectId, PromoteReleaseRequest request,
            ProjectContextFactory projects, HttpContext http, CancellationToken ct) =>
        {
            if (http.Items[DashboardMutationAuthentication.AuthenticatedOperatorItem] is not string actor) return Results.Unauthorized();
            var project = projects.Find(projectId);
            if (project is null) return Results.NotFound();
            try
            {
                var promotion = await project.Deployments.PromoteAsync(project.Options, id, request, actor, ct);
                wakeDelivery?.Invoke();
                return Results.Ok(promotion);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }
}
