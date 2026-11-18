namespace Forge.Core;

/// <summary>Read owner-local design data while retaining pre-routing history in the primary store.</summary>
public static class DesignerStoreRouting
{
    public static async Task<IReadOnlyList<DesignerRun>> ListRunsAsync(ISpecStore? specs,
        DesignerRunStore primary, string? specId, int limit, CancellationToken ct = default)
    {
        // The legacy unscoped endpoint is primary history, not cross-project administration.
        if (specId is null) return await primary.ListAsync(null, limit, ct);
        var owner = specs is ProjectRoutingSpecStore routing ? routing.ResolveOwner(specId) : specs;
        var history = await primary.ListAsync(specId, limit, ct);
        if (owner is not SpecStore projectOwner || primary.UsesDatabase(projectOwner.Database)) return history;
        var current = await new DesignerRunStore(projectOwner.Database).ListAsync(specId, limit, ct);
        // Do not deduplicate numeric run IDs across databases.
        return history.Concat(current).OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id).Take(limit).ToArray();
    }

    public static async Task<IReadOnlyList<DesignArtifact>> ListArtifactsAsync(ISpecStore? specs,
        DesignArtifactStore primary, string id, bool byProject, CancellationToken ct = default)
    {
        if (byProject && specs is ProjectRoutingSpecStore projectRouting)
        {
            // Scope by the registered physical owner, never by a mutable project_id column.
            if (projectRouting.FindRegisteredStore(id) is not SpecStore projectOwner) return [];
            var ids = await projectOwner.ListOwnedIdsAsync(ct);
            var owned = new List<DesignArtifact>(await primary.ListBySpecsAsync(ids, ct));
            if (!primary.UsesDatabase(projectOwner.Database))
                owned.AddRange(await new DesignArtifactStore(projectOwner.Database).ListBySpecsAsync(ids, ct));
            return owned.DistinctBy(a => a.Id).OrderBy(a => a.CreatedAt).ToArray();
        }
        var owner = specs is ProjectRoutingSpecStore routing
            ? routing.ResolveOwner(id) as SpecStore : specs as SpecStore;
        var results = new List<DesignArtifact>(byProject
            ? await primary.ListByProjectAsync(id, ct) : await primary.ListBySpecAsync(id, ct: ct));
        if (owner is not null && !primary.UsesDatabase(owner.Database))
        {
            var scoped = new DesignArtifactStore(owner.Database);
            results.AddRange(byProject ? await scoped.ListByProjectAsync(id, ct) : await scoped.ListBySpecAsync(id, ct: ct));
        }
        return results.DistinctBy(a => a.Id).OrderBy(a => a.CreatedAt).ToArray();
    }
}
