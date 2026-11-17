using Forge.Configuration;
using Forge.Core;
using Forge.Projects;
using Xunit;

namespace Forge.Tests;

public sealed class DeploymentProfileTests
{
    [Fact]
    public async Task LiveRegistryRetainsTrustedReleaseProfileAfterRefresh()
    {
        var path = TempRoot.Instance.NewDbPath("deployment-profile");
        await using var issues = new IssueStore(path);
        await using var store = new ProjectStore(issues);
        await store.UpsertAsync(new("forge", "Forge", "https://example.invalid/forge.git", "main"));
        var profile = new DeploymentOptions { Kind = DeploymentKind.Kubernetes, KubernetesImageCatalog = "forge-image-catalog" };
        await using var factory = new ProjectContextFactory(store, Path.GetDirectoryName(path)!,
            new Dictionary<string, string> { ["forge"] = path }, deploymentProfiles: new Dictionary<string, DeploymentOptions> { ["forge"] = profile });
        Assert.Same(profile, Assert.Single(factory.KnownProjects).Deployment);
        Assert.Same(profile, factory.Find("forge")!.Options.Deployment);
        await store.UpdateRolesAsync("forge", new Dictionary<string, int> { ["coredev"] = 1 });
        Assert.Same(profile, factory.Find("forge")!.Options.Deployment);
    }
}
