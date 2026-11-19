using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Deploy;
using Xunit;

namespace Forge.Tests;

public sealed class DeploymentEnvironmentPolicyTests : IDisposable
{
    private readonly IssueStore issues = new(TempRoot.Instance.NewDbPath("environment-policy"), "forge");
    private DeploymentStore Store => new(issues.Db);
    private readonly ProjectOptions project = new() { Id = "forge", Deployment = new() {
        Kind = DeploymentKind.Kubernetes, SprintEnvironment = "uat", Environments = new() {
            ["uat"] = new() { DisplayName = "UAT", KubernetesNamespace = "forge-uat", KubernetesDatasetId = Guid.NewGuid().ToString(), PromoteTo = ["prod"] },
            ["prod"] = new() { DisplayName = "Production", KubernetesNamespace = "forge", KubernetesDatasetId = Guid.NewGuid().ToString() } } } };
    private const string Actor = "operator";
    public void Dispose() => issues.Dispose();

    [Fact]
    public async Task EditingPolicyChecksRevisionAndProvisionedBindings()
    {
        var initial = await Store.GetEnvironmentPolicyAsync(project, default);
        var policy = initial with { Environments = initial.Environments.Select(e => e.Name == "uat" ? e with { RequireApproval = false } : e).ToList() };
        var saved = await Store.SaveEnvironmentPolicyAsync(project, policy, Actor, default);
        Assert.Equal(1, saved.Revision);
        Assert.False((await Store.GetEffectiveProjectAsync(project, default)).Deployment!.Environments["uat"].RequireApproval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveEnvironmentPolicyAsync(project, initial, Actor, default));
        var altered = saved with { Environments = saved.Environments.Select(e => e with { KubernetesNamespace = "other-namespace" }).ToList() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.SaveEnvironmentPolicyAsync(project, altered, Actor, default));
    }

    private async Task<DeploymentCandidate> Installed(string operation, char source, char image)
    {
        var sha = new string(source, 40);
        var receipt = new TrustedImageReceipt(sha, "ghcr.io/xyrces/forge@sha256:" + new string(image, 64), true, true,
            "controller-image.yml", "1", "Xyrces/Forge", "main", "2", "sha256:" + new string('f', 64), IssueStore.CurrentSchemaVersion, "ci.yml", "3");
        var snapshot = DeploymentEnvironmentResolver.Capture(project, "uat");
        var candidate = new DeploymentCandidate(operation, project.Id, sha, "Verified installer release", DeploymentStatus.Deployed, DateTime.UtcNow,
            Actor, null, null, null, null, null, ReceiptJson: JsonSerializer.Serialize(receipt, DeploymentStore.EnvironmentJson),
            EnvironmentName: "uat", TargetJson: JsonSerializer.Serialize(snapshot, DeploymentStore.EnvironmentJson));
        return await Store.RecordVerifiedInstallationAsync(candidate, "verified", default);
    }

    [Fact]
    public async Task PromotionPinsSelectedOlderReleaseAndSerializesTargetAdmission()
    {
        var older = await Installed("deploy-older", 'a', 'b');
        var newer = await Installed("deploy-newer", 'c', 'd');
        var request = new PromoteReleaseRequest("prod", older.CommitSha, "ghcr.io/xyrces/forge@sha256:" + new string('b', 64));
        var promotions = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Store.PromoteAsync(project, older.Id, request, Actor, default)));
        Assert.Equal(promotions[0].Id, promotions[1].Id);
        var candidate = promotions[0];
        Assert.Equal(older.Id, candidate.SourceCandidateId);
        Assert.Equal(older.ReceiptJson, candidate.ReceiptJson);
        Assert.Equal(older.CommitSha, candidate.CommitSha);
        Assert.Equal("prod", candidate.EnvironmentName);
        Assert.Equal(Actor, candidate.ApprovedBy);
        Assert.Null(candidate.CompletionId);
        var next = new PromoteReleaseRequest("prod", newer.CommitSha, "ghcr.io/xyrces/forge@sha256:" + new string('d', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.PromoteAsync(project, newer.Id, next, Actor, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.PromoteAsync(project, older.Id, next, Actor, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store.PromoteAsync(project with { Id = "other" }, older.Id, request, Actor, default));
        await Store.MarkIndependentProgressAsync(candidate.Id, new(false, "rolled back"));
        Assert.NotEqual(candidate.Id, (await Store.PromoteAsync(project, older.Id, request, Actor, default)).Id);
    }
}
