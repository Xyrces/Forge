using PortHorizon.Agents.Core;
using Xunit;

namespace PortHorizon.Agents.Tests;

public class SpecStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;

    public SpecStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ph-spec-{Guid.NewGuid():N}.db");
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task CreateAsync_StartsAtVersionOne_DraftStatus()
    {
        var spec = await _specs.CreateAsync(new NewSpec(
            ProjectId: "P", Title: "Onboarding flow",
            Body: "Step 1. Step 2.", Author: "alice"));
        Assert.StartsWith("spec-", spec.Id);
        Assert.Equal(SpecStatus.Draft, spec.Status);
        Assert.Equal(1, spec.CurrentVersion);
        Assert.Equal("alice", spec.Author);
        Assert.Equal("Step 1. Step 2.", spec.Body);
    }

    [Fact]
    public async Task CreateAsync_MissingProjectId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _specs.CreateAsync(new NewSpec(ProjectId: "", Title: "t", Body: "b")));
    }

    [Fact]
    public async Task CreateAsync_MissingTitle_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _specs.CreateAsync(new NewSpec(ProjectId: "P", Title: "", Body: "b")));
    }

    [Fact]
    public async Task GetAsync_RoundTripsCurrentBody()
    {
        var created = await _specs.CreateAsync(new NewSpec("P", "T", "first body"));
        var fetched = await _specs.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("first body", fetched!.Body);
        Assert.Equal(1, fetched.CurrentVersion);
    }

    [Fact]
    public async Task GetAsync_Missing_ReturnsNull()
    {
        Assert.Null(await _specs.GetAsync("spec-missing"));
    }

    [Fact]
    public async Task UpdateBodyAsync_AppendsNewVersion_BumpsCurrent()
    {
        var created = await _specs.CreateAsync(new NewSpec("P", "T", "v1 body"));
        var updated = await _specs.UpdateBodyAsync(created.Id,
            new UpdateSpecBody("v2 body", Author: "bob"));
        Assert.Equal(2, updated.CurrentVersion);
        Assert.Equal("v2 body", updated.Body);
        Assert.Equal("bob", updated.Author);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task ListVersionsAsync_ReturnsHistoryDescending()
    {
        var created = await _specs.CreateAsync(new NewSpec("P", "T", "v1"));
        await _specs.UpdateBodyAsync(created.Id, new UpdateSpecBody("v2"));
        await _specs.UpdateBodyAsync(created.Id, new UpdateSpecBody("v3"));

        var versions = await _specs.ListVersionsAsync(created.Id);
        Assert.Equal(3, versions.Count);
        Assert.Equal(3, versions[0].Version);
        Assert.Equal(2, versions[1].Version);
        Assert.Equal(1, versions[2].Version);
        Assert.Equal("v3", versions[0].Body);
        Assert.Equal("v2", versions[1].Body);
        Assert.Equal("v1", versions[2].Body);
    }

    [Fact]
    public async Task SetStatusAsync_DoesNotChangeVersion()
    {
        var created = await _specs.CreateAsync(new NewSpec("P", "T", "v1"));
        await _specs.UpdateBodyAsync(created.Id, new UpdateSpecBody("v2"));
        var approved = await _specs.SetStatusAsync(created.Id, SpecStatus.Approved);
        Assert.Equal(SpecStatus.Approved, approved.Status);
        Assert.Equal(2, approved.CurrentVersion); // unchanged
        // Versions history unchanged.
        var versions = await _specs.ListVersionsAsync(created.Id);
        Assert.Equal(2, versions.Count);
    }

    [Fact]
    public async Task ListAsync_FilterByProjectAndStatus()
    {
        var s1 = await _specs.CreateAsync(new NewSpec("P1", "Spec in P1 draft", "x"));
        var s2 = await _specs.CreateAsync(new NewSpec("P1", "Spec in P1 second", "y"));
        var s3 = await _specs.CreateAsync(new NewSpec("P2", "Spec in P2", "z"));
        await _specs.SetStatusAsync(s1.Id, SpecStatus.Approved);

        var p1All = await _specs.ListAsync("P1", status: null, default);
        Assert.Equal(2, p1All.Count);

        var p1Draft = await _specs.ListAsync("P1", SpecStatus.Draft, default);
        Assert.Single(p1Draft);
        Assert.Equal(s2.Id, p1Draft[0].Id);

        var all = await _specs.ListAsync(null, null, default);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task CreateAsync_LinksParentIssueIdAndParentSpecId()
    {
        var parentIssue = await _issues.CreateAsync(new NewIssue(
            Type: "epic", Title: "parent", Description: ""));
        var parentSpec = await _specs.CreateAsync(new NewSpec("P", "Parent", "x"));
        var child = await _specs.CreateAsync(new NewSpec("P", "Child", "y",
            ParentIssueId: parentIssue.Id, ParentSpecId: parentSpec.Id));
        var fetched = await _specs.GetAsync(child.Id);
        Assert.Equal(parentIssue.Id, fetched!.ParentIssueId);
        Assert.Equal(parentSpec.Id, fetched.ParentSpecId);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSpecAndCascadesVersions()
    {
        var created = await _specs.CreateAsync(new NewSpec("P", "T", "v1"));
        await _specs.UpdateBodyAsync(created.Id, new UpdateSpecBody("v2"));
        await _specs.DeleteAsync(created.Id);
        Assert.Null(await _specs.GetAsync(created.Id));
        Assert.Empty(await _specs.ListVersionsAsync(created.Id));
    }
}

public class NullSpecStoreTests
{
    [Fact]
    public async Task ListAsync_ReturnsEmpty()
    {
        var s = new NullSpecStore();
        Assert.Empty(await s.ListAsync(null, null, default));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull()
    {
        var s = new NullSpecStore();
        Assert.Null(await s.GetAsync("anything", default));
    }

    [Fact]
    public async Task CreateAsync_Throws()
    {
        var s = new NullSpecStore();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => s.CreateAsync(new NewSpec("P", "T", "b"), default));
    }
}
