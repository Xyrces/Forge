using Forge.Configuration;
using Forge.Core;

namespace Forge.Projects;

/// <summary>
/// Resolves the effective <see cref="GitHubOptions"/> for a project's
/// git-over-HTTPS operations (clone/sync): the per-project
/// <c>github_token</c> secret overrides the global <c>GITHUB_TOKEN</c>
/// env / <c>github.token</c> config. A missing row or a decrypt/store
/// failure (keyring rotation) falls back to the global token. Same
/// override rule <c>ProjectDispatchBundleFactory</c> applies for
/// push/PR ops.
/// </summary>
public static class GitHubTokenResolver
{
    /// <summary>
    /// Resolve the raw token with explicit-first precedence: an
    /// explicitly supplied token (e.g. entered in the UI) wins, then
    /// the per-project <c>github_token</c> secret, then the global
    /// config. Returns null when no source yields a token.
    /// </summary>
    public static async Task<string?> ResolveTokenAsync(
        string? explicitToken,
        string? projectId,
        GitHubOptions? global,
        ISecretStore? secrets,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(explicitToken)) return explicitToken;
        if (secrets is not null && !string.IsNullOrEmpty(projectId))
        {
            string? perProject = null;
            try
            {
                perProject = await secrets.GetPlaintextAsync(projectId, SecretKinds.GitHubToken, ct);
            }
            catch
            {
                // decrypt or store failure → fall back to the global token
            }
            if (!string.IsNullOrEmpty(perProject)) return perProject;
        }
        return string.IsNullOrEmpty(global?.Token) ? null : global!.Token;
    }

    public static async Task<GitHubOptions?> ResolveAsync(
        string projectId,
        GitHubOptions? global,
        ISecretStore? secrets,
        CancellationToken ct = default)
    {
        if (secrets is not null)
        {
            string? perProject = null;
            try
            {
                perProject = await secrets.GetPlaintextAsync(projectId, SecretKinds.GitHubToken, ct);
            }
            catch
            {
                // decrypt or store failure → fall back to the global token
            }
            if (!string.IsNullOrEmpty(perProject))
            {
                return new GitHubOptions
                {
                    Owner = global?.Owner ?? string.Empty,
                    Repo = global?.Repo ?? string.Empty,
                    Token = perProject,
                };
            }
        }
        return global;
    }
}
