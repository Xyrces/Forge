using System.Text.Json;
using Forge.Configuration;
using Forge.Projects;
using Xunit;

namespace Forge.Tests;

[Collection("Controller App environment")]
public sealed class ReleaseCatalogCredentialsTests
{
    [Fact]
    public async Task CatalogUsesItsProjectsAppAndNeverFallsBackOnAppFailure()
    {
        var root = TempRoot.Instance.NewDirectory("release-catalog-credentials");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "apps.json");
        var publisher = new GitHubAppIdentities.App(1, 11, "publisher", Path.Combine(root, "missing.pem"));
        var reviewer = new GitHubAppIdentities.App(2, 22, "reviewer", Path.Combine(root, "reviewer.pem"));
        var repository = new GitHubAppIdentities.Repository("example", "app-project", publisher, reviewer);
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { repositories = new[] { repository } }));
        var previous = Environment.GetEnvironmentVariable("FORGE_GITHUB_APPS_FILE");
        try
        {
            Environment.SetEnvironmentVariable("FORGE_GITHUB_APPS_FILE", config);
            var legacy = new GitHubOptions { Token = "fixture-only" };
            var project = new ProjectOptions { Id = "app-project", RepoUrl = "https://github.com/example/app-project.git" };
            await Assert.ThrowsAsync<FileNotFoundException>(() => GitHubTokenResolver.ResolveReleaseTokenAsync(project, legacy, null, default));
            var other = new ProjectOptions { Id = "legacy", RepoUrl = "https://github.com/example/other" };
            Assert.Equal("fixture-only", await GitHubTokenResolver.ResolveReleaseTokenAsync(other, legacy, null, default));
            Assert.Same(GitHubAppIdentities.CredentialsFor(repository, false), GitHubAppIdentities.CredentialsFor(repository, false));
            Assert.NotSame(GitHubAppIdentities.CredentialsFor(repository, false), GitHubAppIdentities.CredentialsFor(repository, true));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_GITHUB_APPS_FILE", previous); }
    }
}
