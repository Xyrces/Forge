using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Forge.Configuration;
using Forge.Deploy;
using Forge.Orchestrator.Slots;
using Forge.Projects;

namespace Forge.Dashboard;

// P8: operator-facing deployment approval surface. Deliberately
// decoupled from git merges -- a deployment candidate only comes into
// existence when an operator (or, later, an automated policy) POSTs a
// specific commit sha to /api/deployments, so a burst of merges to a
// project's main branch never forces an unplanned redeploy. See
// docs/deployment-pipeline.md for the full flow.
public static class DeploymentsEndpoints
{
    public static IEndpointRouteBuilder MapDeploymentsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/deployments");

        group.MapGet("/", ListAsync)
             .WithName("ListDeployments")
             .WithSummary("List deployment candidates, optionally filtered to one project.");

        group.MapGet("/commits", ListCommitsAsync)
             .WithName("ListDeployableCommits")
             .WithSummary("Recent commits on a project's repo, for the 'pick a commit to deploy' picker.");

        group.MapPost("/", CreateAsync)
             .WithName("RequestDeployment")
             .WithSummary("Register a commit as a deployment candidate; kicks off the build/test gate if the project requires one.");

        group.MapPost("/{id}/approve", ApproveAsync)
             .WithName("ApproveDeployment")
             .WithSummary("Approve a candidate and run the project's configured deployment executor.");

        group.MapPost("/{id}/reject", RejectAsync)
             .WithName("RejectDeployment")
             .WithSummary("Reject a candidate. Terminal -- create a new candidate to try again.");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? projectId, ProjectContextFactory factory, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var ctx = factory.Find(projectId);
            if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
            return Results.Ok(await ctx.Deployments.ListAsync(projectId, ct: ct));
        }

        var all = new List<DeploymentCandidate>();
        foreach (var p in factory.KnownProjects)
        {
            var ctx = factory.Find(p.Id);
            if (ctx is null) continue;
            all.AddRange(await ctx.Deployments.ListAsync(p.Id, ct: ct));
        }
        return Results.Ok(all.OrderByDescending(d => d.RequestedAt).ToList());
    }

    private static async Task<IResult> ListCommitsAsync(
        string projectId, int? limit, ProjectContextFactory factory, CancellationToken ct)
    {
        var ctx = factory.Find(projectId);
        if (ctx is null) return Results.NotFound(new { error = "project not found", projectId });
        if (!Directory.Exists(Path.Combine(ctx.Options.Root, ".git")))
            return Results.Ok(Array.Empty<CommitRow>());

        var rows = await RunGitLogAsync(ctx.Options.Root, limit ?? 20, ct);
        return Results.Ok(rows);
    }

    // Full 40-char sha or any git-abbreviated prefix (git's default
    // minimum is 4, but 7 is the practical floor before collisions
    // become a real concern). This value is later interpolated
    // directly into `git` command-line arguments and filesystem paths
    // (DeploymentBuildRunner, SelfHostedSystemdServiceDeploymentExecutor,
    // GetCommitSummaryAsync below) -- rejecting anything that isn't a
    // plain hex string here is what keeps those call sites safe,
    // rather than re-validating (or forgetting to) at each one.
    private static readonly Regex CommitShaPattern = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

    private static async Task<IResult> CreateAsync(
        CreateDeploymentRequest? body, ProjectContextFactory factory, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.CommitSha))
            return Results.BadRequest(new { error = "projectId and commitSha are required" });

        if (!CommitShaPattern.IsMatch(body.CommitSha))
            return Results.BadRequest(new { error = "commitSha must be a 7-40 character hex string" });

        var ctx = factory.Find(body.ProjectId);
        if (ctx is null) return Results.NotFound(new { error = "project not found", projectId = body.ProjectId });

        var summary = await GetCommitSummaryAsync(ctx.Options.Root, body.CommitSha, ct);
        var candidate = await ctx.Deployments.CreateAsync(body.ProjectId, body.CommitSha, summary, body.RequestedBy, ct);

        // The build/test gate can take minutes; run it off the request
        // thread and let the operator poll GET /api/deployments for
        // status transitions (Pending -> BuildRunning -> BuildPassed/Failed).
        var runner = new DeploymentBuildRunner(ctx.Deployments, loggerFactory.CreateLogger<DeploymentBuildRunner>());
        _ = Task.Run(() => runner.RunAsync(ctx.Options, candidate, CancellationToken.None), CancellationToken.None);

        return Results.Ok(candidate);
    }

    private static async Task<IResult> ApproveAsync(
        string id, string projectId, ApproveDeploymentRequest? body,
        ProjectContextFactory factory, SlotTable slots, DeploymentExecutorFactory executors,
        CancellationToken ct)
    {
        var (ownerCtx, candidate) = await ResolveOwnedCandidateAsync(id, projectId, factory, ct);
        if (ownerCtx is null || candidate is null)
            return Results.NotFound(new { error = "deployment not found", id, projectId });

        if (candidate.Status is not (DeploymentStatus.Pending or DeploymentStatus.BuildPassed))
            return Results.BadRequest(new { error = $"cannot approve a deployment in status {candidate.Status}" });

        var project = ownerCtx.Options;
        var force = body?.Force ?? false;

        // Redeploying Forge itself bounces the ONE process running the
        // dispatch loop for every registered project, not just this
        // one -- surface that broadly before letting it through.
        if (project.Deployment?.Kind == DeploymentKind.SelfHostedSystemdService && !force)
        {
            var inFlight = slots.Snapshot().Where(s => s.InFlight > 0).ToList();
            if (inFlight.Count > 0)
            {
                return Results.Json(new
                {
                    error = "in_flight_tasks",
                    message = $"{inFlight.Sum(s => s.InFlight)} task(s) in flight across " +
                              $"{inFlight.Select(s => s.ProjectId).Distinct().Count()} project(s). Deploying now " +
                              "restarts the Forge service and will interrupt them. Retry with force=true to proceed anyway.",
                    inFlight,
                }, statusCode: 409);
            }
        }

        // The status check above is a fast-path rejection for the
        // common case (wrong status, no lock needed); TryApproveAsync
        // is the real guard -- its WHERE clause re-checks status
        // atomically at write time, so a second request that raced
        // past the check above and lost the write gets told to back
        // off instead of also launching a deployment executor.
        if (!await ownerCtx.Deployments.TryApproveAsync(id, body?.ApprovedBy, ct))
            return Results.Conflict(new { error = "already_approved", message = "This deployment was approved by another request first.", id });

        var executor = executors.Create(project);
        if (executor is null)
        {
            const string msg = "No deployment executor configured for this project (DeploymentKind.None, or Deployment is unset).";
            await ownerCtx.Deployments.MarkDeployFailedAsync(id, msg, ct);
            return Results.BadRequest(new { error = msg });
        }

        await ownerCtx.Deployments.SetStatusAsync(id, DeploymentStatus.Deploying, ct);
        var result = await executor.ExecuteAsync(project, candidate, ct);

        if (result.StillInProgress)
        {
            // Reserved for future executors that hand off to an
            // external job runner. Today all executors are
            // synchronous (Script, SelfHostedSystemdService).
            return Results.Ok(new { id, status = "Deploying", message = result.Log });
        }

        if (result.Success)
        {
            await ownerCtx.Deployments.MarkDeployedAsync(id, result.Log, ct);
            return Results.Ok(new { id, status = "Deployed", message = result.Log });
        }

        await ownerCtx.Deployments.MarkDeployFailedAsync(id, result.Log, ct);
        return Results.Json(new { id, status = "DeployFailed", message = result.Log }, statusCode: 500);
    }

    private static async Task<IResult> RejectAsync(string id, string projectId, ProjectContextFactory factory, CancellationToken ct)
    {
        var (ownerCtx, candidate) = await ResolveOwnedCandidateAsync(id, projectId, factory, ct);
        if (ownerCtx is null || candidate is null)
            return Results.NotFound(new { error = "deployment not found", id, projectId });

        if (!await ownerCtx.Deployments.TryRejectAsync(id, ct))
            return Results.BadRequest(new { error = $"cannot reject a deployment in status {candidate.Status}" });

        return Results.Ok(new { id, status = "Rejected" });
    }

    // Callers must name the owning project explicitly (rather than
    // this endpoint scanning every known project for a matching id) --
    // besides avoiding an O(projects) sqlite lookup per approve/reject,
    // it means a caller can never approve/reject a deployment belonging
    // to a project it didn't ask for by id collision or guesswork.
    private static async Task<(ProjectContext? Ctx, DeploymentCandidate? Candidate)> ResolveOwnedCandidateAsync(
        string id, string projectId, ProjectContextFactory factory, CancellationToken ct)
    {
        var ctx = factory.Find(projectId);
        if (ctx is null) return (null, null);

        var candidate = await ctx.Deployments.GetAsync(id, ct);
        if (candidate is null || candidate.ProjectId != projectId) return (null, null);

        return (ctx, candidate);
    }

    private static async Task<string?> GetCommitSummaryAsync(string root, string commitSha, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(root, ".git"))) return null;
        try
        {
            var (exitCode, stdout) = await RunGitAsync(root, $"log -1 --pretty=format:%h %s \"{commitSha}\"", ct);
            return exitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<CommitRow>> RunGitLogAsync(string root, int limit, CancellationToken ct)
    {
        var (exitCode, stdout) = await RunGitAsync(
            root, $"log -n {limit} --date=iso-strict --pretty=format:%H%x1f%h%x1f%s%x1f%an%x1f%ad", ct);
        if (exitCode != 0) return Array.Empty<CommitRow>();

        var rows = new List<CommitRow>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\u001f');
            if (parts.Length != 5) continue;
            rows.Add(new CommitRow(parts[0], parts[1], parts[2], parts[3], parts[4]));
        }
        return rows;
    }

    private static async Task<(int ExitCode, string Stdout)> RunGitAsync(string root, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout);
    }

    public sealed record CreateDeploymentRequest(string ProjectId, string CommitSha, string? RequestedBy);
    public sealed record ApproveDeploymentRequest(string? ApprovedBy, bool Force = false);
    public sealed record CommitRow(string Sha, string ShortSha, string Subject, string Author, string DateIso);
}
