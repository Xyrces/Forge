using Forge.Configuration;
using Xunit;

namespace Forge.Tests;

public sealed class DeploymentEnvironmentResolverTests
{
    private static ProjectOptions Project() => new() { Deployment = new() {
        Kind = DeploymentKind.Kubernetes, SprintEnvironment = "uat", Environments = new() {
            ["uat"] = new() { DisplayName = "UAT", KubernetesNamespace = "forge", KubernetesDatasetId = "d4a5fb7c-de2d-4ce5-a297-f8230e79e761", RequireApproval = false, PromoteTo = ["prod"] },
            ["prod"] = new() { DisplayName = "Production", KubernetesNamespace = "forge-prod", KubernetesDatasetId = "9de56a7a-7f68-4c2b-85de-a0b1810cd716" } } } };

    [Fact]
    public void CapturedTargetSurvivesConfigurationChangesWithoutRedirectingRollback()
    {
        var project = Project();
        var snapshot = DeploymentEnvironmentResolver.Capture(project, "prod");
        project.Deployment!.Environments["prod"].KubernetesNamespace = "replacement";
        project.Deployment.Environments["prod"].KubernetesDatasetId = Guid.NewGuid().ToString();
        var resolved = DeploymentEnvironmentResolver.Apply(project, snapshot);
        Assert.Equal("forge-prod", resolved.Deployment!.KubernetesNamespace);
        Assert.Equal("9de56a7a-7f68-4c2b-85de-a0b1810cd716", resolved.Deployment.KubernetesDatasetId);
        Assert.Equal("forge", project.Deployment.KubernetesNamespace);
    }

    [Fact]
    public void TargetsCannotShareNamespaceOrDataset()
    {
        var project = Project();
        project.Deployment!.Environments["prod"].KubernetesNamespace = "forge";
        Assert.Throws<InvalidOperationException>(() => DeploymentEnvironmentResolver.Validate(project));
        project = Project();
        project.Deployment!.Environments["prod"].KubernetesDatasetId = project.Deployment.Environments["uat"].KubernetesDatasetId;
        Assert.Throws<InvalidOperationException>(() => DeploymentEnvironmentResolver.Validate(project));
    }

    [Fact]
    public void GatesAndPromotionRoutesAreExplicit()
    {
        var project = Project();
        DeploymentEnvironmentResolver.Validate(project);
        Assert.False(project.Deployment!.Environments["uat"].RequireApproval);
        Assert.True(project.Deployment.Environments["prod"].RequireApproval);
        project.Deployment.Environments["uat"].PromoteTo.Add("missing");
        Assert.Throws<InvalidOperationException>(() => DeploymentEnvironmentResolver.Validate(project));
    }

    [Fact]
    public void UnknownSprintTargetFailsClosed()
    {
        var project = Project();
        project.Deployment!.SprintEnvironment = "missing";
        Assert.Throws<InvalidOperationException>(() => DeploymentEnvironmentResolver.Validate(project));
        Assert.Throws<InvalidOperationException>(() => DeploymentEnvironmentResolver.Capture(Project(), "missing"));
    }
}
