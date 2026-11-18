namespace Forge.Core;

/// <summary>Find project-owned design records, including history written before owner routing.</summary>
public static class DesignerStoreRouting
{
    public static async Task<IReadOnlyList<DesignerRun>> ListRunsAsync(ISpecStore? specs,
        DesignerRunStore primary, string? specId, int limit, CancellationToken ct = default)
    {
        if (specId is null) return await primary.ListAsync(null, limit, ct);
        if (specs is ProjectRoutingSpecStore scope && scope.ResolveOwner(specId) is null) return [];
        var history = new List<DesignerRun>(await primary.ListAsync(specId, limit, ct));
        var owners = specs is ProjectRoutingSpecStore routing
            ? routing.ArtifactStores
            : specs is SpecStore single ? [single] : Enumerable.Empty<SpecStore>();
        foreach (var store in owners)
            if (!primary.UsesDatabase(store.Database))
                history.AddRange(await new DesignerRunStore(store.Database).ListAsync(specId, limit, ct));
        // Numeric run IDs are local to their database.
        return history.OrderByDescending(run => run.Ts).ThenByDescending(run => run.Id).Take(limit).ToArray();
    }

    public static async Task<IReadOnlyList<DesignArtifact>> ListArtifactsAsync(ISpecStore? specs,
        DesignArtifactStore primary, string id, bool byProject, CancellationToken ct = default)
    {
        if (specs is ProjectRoutingSpecStore routing)
        {
            var owner = (byProject ? routing.FindRegisteredStore(id) : routing.ResolveOwner(id)) as SpecStore;
            if (owner is null) return [];
            IReadOnlyList<string> ids = byProject ? await owner.ListOwnedIdsAsync(ct) : [id];
            var owned = await new DesignArtifactStore(owner.Database).ListBySpecsAsync(ids, ct);
            var historical = new List<DesignArtifact>();
            // Owner-local records take precedence. Historical IDs remain tied to physically owned specs.
            foreach (var store in routing.ArtifactStores)
            {
                if (new DesignArtifactStore(store.Database).UsesDatabase(owner.Database)) continue;
                historical.AddRange(await new DesignArtifactStore(store.Database).ListBySpecsAsync(ids, ct));
            }
            return ArtifactHistory.IncludeUnambiguousHistory(owned, historical, artifact => artifact.Id)
                .OrderBy(artifact => artifact.CreatedAt).ToArray();
        }
        var results = new List<DesignArtifact>(byProject
            ? await primary.ListByProjectAsync(id, ct) : await primary.ListBySpecAsync(id, ct: ct));
        if (specs is SpecStore local && !primary.UsesDatabase(local.Database))
        {
            var scoped = new DesignArtifactStore(local.Database);
            results.AddRange(byProject ? await scoped.ListByProjectAsync(id, ct) : await scoped.ListBySpecAsync(id, ct: ct));
        }
        return results.DistinctBy(artifact => artifact.Id).OrderBy(artifact => artifact.CreatedAt).ToArray();
    }
}
