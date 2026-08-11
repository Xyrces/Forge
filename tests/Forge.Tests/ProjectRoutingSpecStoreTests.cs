using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// ProjectRoutingSpecStore: writes route by NewSpec.ProjectId,
/// id-addressed ops probe + cache the owner (spec ids are random
/// hex — safe cross-store keys), scoped reads hit the owning store,
/// UNSCOPED reads throw (2026-08-09 isolation rule — the explicit
/// ListAcrossProjectsAsync fan-out is for pipeline schedulers and
/// the unified admin view only).
/// </summary>
public class ProjectRoutingSpecStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly IssueStore _primaryIssues;
    private readonly IssueStore _phIssues;
    private readonly SpecStore _primary;
    private readonly SpecStore _ph;
    private readonly ProjectRoutingSpecStore _routing;

    public ProjectRoutingSpecStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ph-rspec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _primaryIssues = new IssueStore(Path.Combine(_dir, "primary.db"));
        _phIssues = new IssueStore(Path.Combine(_dir, "ph.db"));
        _primary = new SpecStore(_primaryIssues);
        _ph = new SpecStore(_phIssues);
        _routing = new ProjectRoutingSpecStore(
            _primary,
            findByProject: pid => pid == "porthorizon" ? _ph : null,
            allProjectStores: () => new ISpecStore[] { _ph });
    }

    public void Dispose()
    {
        try { _primaryIssues.Dispose(); } catch { }
        try { _phIssues.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static NewSpec Spec(string project, string title) =>
        new(ProjectId: project, Title: title, Body: "body " + title);

    [Fact]
    public async Task Create_RoutesToOwningProject()
    {
        var ph = await _routing.CreateAsync(Spec("porthorizon", "ph spec"));
        var forge = await _routing.CreateAsync(Spec("forge", "forge spec"));

        Assert.NotNull(await _ph.GetAsync(ph.Id));
        Assert.Null(await _primary.GetAsync(ph.Id));
        Assert.NotNull(await _primary.GetAsync(forge.Id));
        Assert.Null(await _ph.GetAsync(forge.Id));
    }

    [Fact]
    public async Task Get_ProbesAcrossStores()
    {
        var ph = await _ph.CreateAsync(Spec("porthorizon", "hidden in ph"));
        var found = await _routing.GetAsync(ph.Id);
        Assert.NotNull(found);
        Assert.Equal("hidden in ph", found!.Title);
    }

    [Fact]
    public async Task List_ByProject_ReadsOwningStore_NoColumnFilter()
    {
        // Legacy row homed in PH whose project_id column disagrees
        // (stale) must still list under porthorizon.
        var ph = await _ph.CreateAsync(Spec("forge", "stale column value"));
        await _primary.CreateAsync(Spec("forge", "primary spec"));

        var list = await _routing.ListAsync("porthorizon", status: null);
        Assert.Single(list);
        Assert.Equal(ph.Id, list[0].Id);
    }

    [Fact]
    public async Task List_NoProject_Throws_IsolationBoundary()
    {
        // Operator rule 2026-08-09: schema-per-project IS the
        // isolation boundary — an unscoped list on the routing store
        // is a programming error (the 2026-08-09 epic-2 collision
        // came from exactly this), not a quiet fan-out.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _routing.ListAsync(null, status: null));
    }

    [Fact]
    public async Task ListAcrossProjects_ExplicitFanOut_Merges()
    {
        await _primary.CreateAsync(Spec("forge", "a"));
        await _ph.CreateAsync(Spec("porthorizon", "b"));
        var all = await _routing.ListAcrossProjectsAsync(status: null);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ListForPipelineSweep_RoutesToExplicitFanOut()
    {
        await _primary.CreateAsync(Spec("forge", "a"));
        await _ph.CreateAsync(Spec("porthorizon", "b"));
        var all = await _routing.ListForPipelineSweepAsync(status: null, CancellationToken.None);
        Assert.Equal(2, all.Count);
        // Plain per-project store: just the store's own rows.
        var phOnly = await _ph.ListForPipelineSweepAsync(status: null, CancellationToken.None);
        Assert.Single(phOnly);
    }

    [Fact]
    public async Task SetStatus_RoutesByOwnerProbe()
    {
        var ph = await _ph.CreateAsync(Spec("porthorizon", "status me"));
        var updated = await _routing.SetStatusAsync(ph.Id, SpecStatus.Approved);
        Assert.Equal(SpecStatus.Approved, updated.Status);
        Assert.Equal(SpecStatus.Approved, (await _ph.GetAsync(ph.Id))!.Status);
    }

    [Fact]
    public async Task UpdateBody_MissingSpec_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _routing.UpdateBodyAsync("spec-nope", new UpdateSpecBody("x", "author")));
    }
}
