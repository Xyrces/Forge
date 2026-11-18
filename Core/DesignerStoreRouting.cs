namespace Forge.Core;

/// <summary>Read owner-local design data while retaining pre-routing history in the primary store.</summary>
public static class DesignerStoreRouting
{
    public static async Task<IReadOnlyList<DesignerRun>> ListRunsAsync(ISpecStore? specs,
        DesignerRunStore primary, string? specId, int limit, CancellationToken ct = default)
    {
        var stores = new List<DesignerRunStore> { primary };
        IEnumerable<ISpecStore> owners = specs is ProjectRoutingSpecStore routing
            ? specId is null ? routing.ProjectStores : new[] { routing.ResolveOwner(specId) }.OfType<ISpecStore>()
            : specs is null ? [] : [specs];
        foreach (var owner in owners.OfType<SpecStore>())
            if (!stores.Any(s => s.UsesDatabase(owner.Database))) stores.Add(new DesignerRunStore(owner.Database));
        var runs = new List<DesignerRun>();
        foreach (var store in stores) runs.AddRange(await store.ListAsync(specId, limit, ct));
        // Do not deduplicate numeric run IDs across databases.
        return runs.OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id).Take(limit).ToArray();
    }

    public static async Task<IReadOnlyList<DesignArtifact>> ListArtifactsAsync(ISpecStore? specs,
        DesignArtifactStore primary, string id, bool byProject, CancellationToken ct = default)
    {
        var owner = specs is ProjectRoutingSpecStore routing
            ? (byProject ? routing.StoreFor(id) : routing.ResolveOwner(id)) as SpecStore : specs as SpecStore;
        var results = new List<DesignArtifact>(byProject
            ? await primary.ListByProjectAsync(id, ct) : await primary.ListBySpecAsync(id, ct: ct));
        if (owner is not null && !primary.UsesDatabase(owner.Database))
        {
            if (byProject)
                results.AddRange(await primary.ListBySpecsAsync(await owner.ListIdsByProjectAsync(id, ct), ct));
            var scoped = new DesignArtifactStore(owner.Database);
            results.AddRange(byProject ? await scoped.ListByProjectAsync(id, ct) : await scoped.ListBySpecAsync(id, ct: ct));
        }
        return results.DistinctBy(a => a.Id).OrderBy(a => a.CreatedAt).ToArray();
    }
}
