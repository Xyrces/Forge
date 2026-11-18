using System.Net.Http.Headers;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator;

namespace Forge.Projects;

internal interface IGitHubPreflightCredentials
{
    GitHubAppIdentities.Repository? FindApps(string owner, string repository);
    Task<string> AppTokenAsync(GitHubAppIdentities.Repository repository, bool reviewer, CancellationToken ct);
    Task<string?> LegacyTokenAsync(string projectId, CancellationToken ct);
    Task<RequiredReview> ReviewModeAsync(string projectId, CancellationToken ct);
}

internal sealed class GitHubPreflightCredentials(GitHubOptions global, ISecretStore secrets,
    Func<string, Core.Db.IDbConnectionFactory> projectDatabase) : IGitHubPreflightCredentials
{
    public async Task<RequiredReview> ReviewModeAsync(string projectId, CancellationToken ct)
    {
        await using var memory = new MemoryStore(projectDatabase(projectId));
        return await new PullRequestReviewPolicy(memory).GetModeAsync(ct);
    }

    public GitHubAppIdentities.Repository? FindApps(string owner, string repository)
        => GitHubAppIdentities.Find(owner, repository);

    public Task<string> AppTokenAsync(GitHubAppIdentities.Repository repository, bool reviewer, CancellationToken ct)
        => GitHubAppIdentities.CredentialsFor(repository, reviewer).GetTokenAsync().WaitAsync(ct);

    public Task<string?> LegacyTokenAsync(string projectId, CancellationToken ct)
        => GitHubTokenResolver.ResolveTokenAsync(null, projectId, global, secrets, ct);
}

/// <summary>Read-only GitHub checks. App configuration failures never select legacy credentials.</summary>
internal sealed class GitHubPreflight(HttpClient http, IGitHubPreflightCredentials credentials)
{
    internal sealed record Result(IReadOnlyList<string> Passed, IReadOnlyList<string> Failed);

    public async Task<Result> CheckAsync(IReadOnlyList<ProjectOptions> projects, GitHubOptions global, CancellationToken ct)
    {
        var passed = new List<string>();
        var failed = new List<string>();
        foreach (var project in projects)
        {
            var parsed = ProjectDispatchBundleFactory.ParseGitHubOwnerRepo(project.RepoUrl);
            var owner = parsed?.Owner ?? global.Owner;
            var repository = parsed?.Repo ?? global.Repo;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
            {
                failed.Add($"Project '{project.Id}': GitHub repository is not configured.");
                continue;
            }
            var scope = owner + "/" + repository;
            RequiredReview mode;
            try { mode = await credentials.ReviewModeAsync(project.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed.Add($"Project '{project.Id}' ({scope}): could not read the project's saved review policy.");
                continue;
            }
            GitHubAppIdentities.Repository? apps;
            try { apps = credentials.FindApps(owner, repository); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"GitHub {scope}: App configuration is invalid; personal-token fallback is disabled.");
                continue;
            }
            if (apps is not null)
            {
                if (apps.Publisher.AppId == apps.Reviewer.AppId
                    || string.Equals(apps.Publisher.Slug, apps.Reviewer.Slug, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(apps.Owner, owner, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(apps.Name, repository, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add($"GitHub {scope}: distinct publisher/reviewer identities scoped to this repository are required.");
                    continue;
                }
                foreach (var reviewer in new[] { false, true })
                {
                    var role = reviewer ? "reviewer" : "publisher";
                    var stage = "installation credentials";
                    try
                    {
                        var token = await credentials.AppTokenAsync(apps, reviewer, ct);
                        stage = "repository metadata";
                        await ReadRepositoryAsync(scope, token, ct);
                        stage = "pull-request metadata";
                        await ReadAsync($"repos/{scope}/pulls?state=open&per_page=1", token, ct);
                        if (!reviewer)
                        {
                            stage = "Actions metadata";
                            await ReadAsync($"repos/{scope}/actions/workflows?per_page=1", token, ct);
                        }
                        passed.Add($"GitHub {scope}: {role} App repository/PR reads passed{(reviewer ? "" : "; Actions metadata read passed")}. No review was submitted.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        failed.Add($"GitHub {scope}: {role} App {stage} check failed; verify its installation, selected repository and role permissions. Personal-token fallback is disabled.");
                    }
                }
            }
            else
            {
                if (PullRequestReviewPolicy.ReviewerConfigurationIssue(mode, false) is { } issue)
                {
                    failed.Add($"Project '{project.Id}' ({scope}): {issue}");
                    continue;
                }
                try
                {
                    var token = await credentials.LegacyTokenAsync(project.Id, ct);
                    if (string.IsNullOrWhiteSpace(token) || token.StartsWith("GITHUB_TOKEN", StringComparison.Ordinal))
                        throw new InvalidOperationException("Legacy credential is unset.");
                    await ReadRepositoryAsync(scope, token, ct);
                    passed.Add($"GitHub {scope}: personal-token repository read passed for Human review.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    failed.Add($"GitHub {scope}: legacy token/repository check failed; verify the project's github_token or global token and repository access.");
                }
            }
        }
        return new(passed, failed);
    }

    private async Task ReadRepositoryAsync(string scope, string token, CancellationToken ct)
    {
        using var response = await SendAsync("repos/" + scope, token, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!body.RootElement.TryGetProperty("full_name", out var name)
            || !string.Equals(name.GetString(), scope, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub returned a different repository.");
    }

    private async Task ReadAsync(string path, string token, CancellationToken ct)
    {
        using var response = await SendAsync(path, token, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Forge-Check");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        response.Dispose();
        throw new InvalidOperationException("GitHub metadata read failed.");
    }
}
