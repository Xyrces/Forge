using Forge.Deploy;
using Xunit;

namespace Forge.Tests;

public class DeploymentStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DeploymentStore _store;

    public DeploymentStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ph-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "issues.db");
        // The `deployment` table is created by IssueStore's schema
        // migration (v15), same as every other feature table that
        // shares the issues.db file (SprintProposalAuditStore, etc.) --
        // constructing an IssueStore against the path first guarantees
        // the table exists before DeploymentStore touches it.
        _ = new Core.IssueStore(_dbPath);
        _store = new DeploymentStore(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    [Fact]
    public async Task CreateAsync_PersistsPendingRow()
    {
        var candidate = await _store.CreateAsync("forge", "abc123", "abc123 fix widget", "alice");

        Assert.Equal("forge", candidate.ProjectId);
        Assert.Equal("abc123", candidate.CommitSha);
        Assert.Equal(DeploymentStatus.Pending, candidate.Status);
        Assert.Equal("alice", candidate.RequestedBy);

        var fetched = await _store.GetAsync(candidate.Id);
        Assert.NotNull(fetched);
        Assert.Equal(candidate.Id, fetched!.Id);
        Assert.Equal(DeploymentStatus.Pending, fetched.Status);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync("deploy-doesnotexist"));
    }

    [Fact]
    public async Task ListAsync_FiltersByProjectAndOrdersNewestFirst()
    {
        await _store.CreateAsync("forge", "sha1", null, null);
        await Task.Delay(5);
        await _store.CreateAsync("forge", "sha2", null, null);
        await _store.CreateAsync("other-project", "sha3", null, null);

        var forgeRows = await _store.ListAsync("forge");
        Assert.Equal(2, forgeRows.Count);
        Assert.Equal("sha2", forgeRows[0].CommitSha);
        Assert.Equal("sha1", forgeRows[1].CommitSha);

        var all = await _store.ListAsync();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task AppendBuildLogAsync_TransitionsStatusAndStoresLog()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.AppendBuildLogAsync(candidate.Id, DeploymentStatus.BuildPassed, "build ok\ntest ok");

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.BuildPassed, updated!.Status);
        Assert.Equal("build ok\ntest ok", updated.BuildLog);
    }

    [Fact]
    public async Task TryApproveAsync_SetsApprovedFieldsAndStatus()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        var approved = await _store.TryApproveAsync(candidate.Id, "bob");

        Assert.True(approved);
        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Approved, updated!.Status);
        Assert.Equal("bob", updated.ApprovedBy);
        Assert.NotNull(updated.ApprovedAt);
    }

    [Fact]
    public async Task TryApproveAsync_SecondCallOnAlreadyApprovedRow_ReturnsFalseAndLeavesRowUnchanged()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        Assert.True(await _store.TryApproveAsync(candidate.Id, "alice"));

        var second = await _store.TryApproveAsync(candidate.Id, "bob");

        Assert.False(second);
        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal("alice", updated!.ApprovedBy);
    }

    [Fact]
    public async Task TryRejectAsync_IsTerminal()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        var rejected = await _store.TryRejectAsync(candidate.Id);

        Assert.True(rejected);
        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Rejected, updated!.Status);
    }

    [Fact]
    public async Task TryRejectAsync_AfterApproval_ReturnsFalseAndLeavesRowApproved()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.TryApproveAsync(candidate.Id, "bob");

        var rejected = await _store.TryRejectAsync(candidate.Id);

        Assert.False(rejected);
        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Approved, updated!.Status);
    }

    [Fact]
    public async Task TryRejectAsync_AfterDeployed_ReturnsFalseAndLeavesRowDeployed()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.TryApproveAsync(candidate.Id, "bob");
        await _store.SetStatusAsync(candidate.Id, DeploymentStatus.Deploying);
        await _store.MarkDeployedAsync(candidate.Id, "deployed cleanly");

        var rejected = await _store.TryRejectAsync(candidate.Id);

        Assert.False(rejected);
        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Deployed, updated!.Status);
    }

    [Fact]
    public async Task MarkDeployedAsync_SetsDeployedFieldsAndLog()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.TryApproveAsync(candidate.Id, "bob");
        await _store.SetStatusAsync(candidate.Id, DeploymentStatus.Deploying);
        await _store.MarkDeployedAsync(candidate.Id, "deployed cleanly");

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.Deployed, updated!.Status);
        Assert.Equal("deployed cleanly", updated.DeployLog);
        Assert.NotNull(updated.DeployedAt);
    }

    [Fact]
    public async Task MarkDeployFailedAsync_SetsFailedStatusWithLog()
    {
        var candidate = await _store.CreateAsync("forge", "sha1", null, null);
        await _store.MarkDeployFailedAsync(candidate.Id, "publish failed: disk full");

        var updated = await _store.GetAsync(candidate.Id);
        Assert.Equal(DeploymentStatus.DeployFailed, updated!.Status);
        Assert.Equal("publish failed: disk full", updated.DeployLog);
    }
}
