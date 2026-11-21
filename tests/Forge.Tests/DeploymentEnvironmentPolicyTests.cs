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
    private readonly ProjectOptions project = new() { Id = "forge", RepoUrl = "https://github.com/Xyrces/Forge", Deployment = new() {
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

    private async Task<DeploymentCandidate> Installed(string operation, char source, char image, int schema = IssueStore.CurrentSchemaVersion)
    {
        var sha = new string(source, 40);
        var receipt = new TrustedImageReceipt(sha, "ghcr.io/xyrces/forge@sha256:" + new string(image, 64), true, true,
            "controller-image.yml", "1", "Xyrces/Forge", "main", "2", "sha256:" + new string('f', 64), schema, "ci.yml", "3");
        var snapshot = DeploymentEnvironmentResolver.Capture(project, "uat");
        var candidate = new DeploymentCandidate(operation, project.Id, sha, "Verified installer release", DeploymentStatus.Deployed, DateTime.UtcNow,
            Actor, null, null, null, null, null, ReceiptJson: JsonSerializer.Serialize(receipt, DeploymentStore.EnvironmentJson),
            EnvironmentName: "uat", TargetJson: JsonSerializer.Serialize(snapshot, DeploymentStore.EnvironmentJson));
        return await Store.RecordVerifiedInstallationAsync(candidate, "verified", default);
    }

    [Fact]
    public async Task HistoricalSchemaIsVisibleButCannotOccupyTheTarget()
    {
        var release = await Installed("deploy-historical", 'a', 'b', IssueStore.CurrentSchemaVersion - 1);
        var row = KubernetesDeploymentExecutor.WithPromotionEligibility(project, release);
        Assert.Contains("schema", row.PromotionUnavailableReason);
        var request = new PromoteReleaseRequest("prod", release.CommitSha, "ghcr.io/xyrces/forge@sha256:" + new string('b', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.PromoteAsync(project, release.Id, request, Actor, default));
        Assert.Single(await Store.ListAsync(project.Id));
    }

    [Fact]
    public async Task SprintAdmissionWaitsForActivePromotionOnTheSameTarget()
    {
        var source = await Installed("deploy-uat-base", 'a', 'b');
        var request = new PromoteReleaseRequest("prod", source.CommitSha, "ghcr.io/xyrces/forge@sha256:" + new string('b', 64));
        var promotion = await Store.PromoteAsync(project, source.Id, request, Actor, default);
        var waiting = await Store.CreateAsync(project.Id, new string('c', 40), "waiting", null);
        await using var conn = await issues.Db.OpenAsync();
        using var seed = conn.CreateCommand();
        seed.CommandText = "UPDATE deployment SET status='BuildPassed',environment_name='prod' WHERE id=@id";
        Forge.Core.Db.DbCommandExtensions.AddParam(seed,"@id",waiting.Id);
        await seed.ExecuteNonQueryAsync();
        waiting = (await Store.GetAsync(waiting.Id))!;
        Assert.False(await Store.TryApproveTargetAsync(waiting, Actor, default));
        await Store.MarkIndependentProgressAsync(promotion.Id, new(true, "verified"));
        Assert.True(await Store.TryApproveTargetAsync(waiting, Actor, default));
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
