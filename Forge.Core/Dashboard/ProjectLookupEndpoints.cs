using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Forge.Configuration;
using Forge.Core;
using Forge.Projects;
using Octokit;

namespace Forge.Dashboard;

/// <summary>
/// Pre-registration lookups backing the guided Add Project flow:
/// enter token → list the token's repos → pick one → pick a branch.
/// The token travels in the POST body only, is never logged, and is
/// never returned. Precedence per call: explicit token → per-project
/// github_token secret → global config.
/// </summary>
public static class ProjectLookupEndpoints
{
    public static IEndpointRouteBuilder MapProjectLookupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects/lookup");

        group.MapPost("/repos", ListReposAsync)
             .WithName("LookupGitHubRepos")
             .WithSummary("List the repos a git token can see (GitHub), for the Add Project repo picker.");

        group.MapPost("/branches", ListBranchesAsync)
             .WithName("LookupRepoBranches")
             .WithSummary("List a repo's branches + detected default (git ls-remote), for the Add Project branch picker.");

        return endpoints;
    }

    private static async Task<IResult> ListReposAsync(
        RepoLookupRequest? body,
        Configuration.GitHubOptions github,
        ISecretStore secrets,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Projects.Lookup");
        var token = await GitHubTokenResolver.ResolveTokenAsync(
            body?.Token, body?.Id, github, secrets, ct);
        if (string.IsNullOrEmpty(token))
        {
            return Results.BadRequest(new
            {
                error = "no token available — enter one, or configure github.token / a project github_token secret",
            });
        }

        try
        {
            var client = new GitHubClient(new ProductHeaderValue("forge"))
            {
                Credentials = new Credentials(token),
            };
            var request = new RepositoryRequest
            {
                Affiliation = RepositoryAffiliation.Owner |
                              RepositoryAffiliation.Collaborator |
                              RepositoryAffiliation.OrganizationMember,
                Sort = RepositorySort.Updated,
                Direction = SortDirection.Descending,
            };
            var repos = await client.Repository.GetAllForCurrent(
                request, new ApiOptions { PageSize = 100, PageCount = 3 });
            var rows = repos
                .OrderByDescending(r => r.UpdatedAt)
                .Select(r => new RepoLookupRow(
                    r.Name, r.FullName, r.CloneUrl, r.Private, r.DefaultBranch))
                .ToList();
            return Results.Ok(new { repos = rows });
        }
        catch (AuthorizationException ex)
        {
            // Octokit: 401 → AuthorizationException, 403 → ForbiddenException (a subtype).
            var code = (int)ex.StatusCode;
            return Results.BadRequest(new { error = $"GitHub rejected the token ({code})" });
        }
        catch (RateLimitExceededException)
        {
            return Results.Json(new { error = "GitHub rate limit exceeded; try again shortly" }, statusCode: 429);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repo lookup failed");
            return Results.Json(new { error = $"repo lookup failed: {ex.Message}" }, statusCode: 502);
        }
    }

    private static async Task<IResult> ListBranchesAsync(
        BranchLookupRequest? body,
        Configuration.GitHubOptions github,
        ISecretStore secrets,
        ProjectCloner cloner,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RepoUrl))
            return Results.BadRequest(new { error = "repoUrl is required" });

        var token = await GitHubTokenResolver.ResolveTokenAsync(
            body.Token, body.Id, github, secrets, ct);
        var effective = new GitHubOptions
        {
            Owner = github?.Owner ?? string.Empty,
            Repo = github?.Repo ?? string.Empty,
            Token = token ?? string.Empty,
        };
        var probe = new ProjectOptions { Id = body.Id ?? "lookup", Name = "lookup", RepoUrl = body.RepoUrl };
        var branches = await cloner.ListRemoteBranchesAsync(probe, effective, ct);
        var defaultBranch = await cloner.DetectDefaultBranchAsync(probe, effective, ct);
        if (branches.Count == 0 && defaultBranch is null)
        {
            return Results.Json(new { error = "could not read the remote (auth or URL)" }, statusCode: 502);
        }
        return Results.Ok(new { branches, defaultBranch });
    }

    public sealed record RepoLookupRequest(string? Token, string? Id);
    public sealed record BranchLookupRequest(string? Token, string? Id, string RepoUrl);
    public sealed record RepoLookupRow(
        string Name, string FullName, string Url, bool Private, string? DefaultBranch);
}
