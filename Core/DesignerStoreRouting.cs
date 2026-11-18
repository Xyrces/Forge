namespace Forge.Core;

/// <summary>Find project-owned design records, including history written before owner routing.</summary>
public static class DesignerStoreRouting
{
    public static async Task<IReadOnlyList<DesignerRun>> ListRunsAsync(ISpecStore? specs,
        DesignerRunStore primary, string? specId, int limit, CancellationToken ct = default)
    {
        if (specId is null) return await primary.ListAsync(null, limit, ct);
        var owner = specs is ProjectRoutingSpecStore scope ? scope.ResolveOwner(specId) : specs;
        if (specs is ProjectRoutingSpecStore && owner is null) return [];
        var ownerRuns = owner is SpecStore physical ? new DesignerRunStore(physical.Database) : primary;
        // Stable ordering favors the physical owner when historical databases have
        // the same timestamp and numeric run ID. IDs are allocated per database.
        var history = new List<DesignerRun>(await ownerRuns.ListAsync(specId, limit, ct));
        if (owner is SpecStore owned && !primary.UsesDatabase(owned.Database))
            history.AddRange(await primary.ListAsync(specId, limit, ct));
        var owners = specs is ProjectRoutingSpecStore routing
            ? routing.ArtifactStores
            : specs is SpecStore single ? [single] : Enumerable.Empty<SpecStore>();
        foreach (var store in owners)
            if (!primary.UsesDatabase(store.Database) && !ownerRuns.UsesDatabase(store.Database))
                history.AddRange(await new DesignerRunStore(store.Database).ListAsync(specId, limit, ct));
        // Numeric run IDs are local to their database.
        return history.OrderByDescending(run => run.Ts).ThenByDescending(run => run.Id).Take(limit).ToArray();
    }

    public static async Task<IReadOnlyList<DesignArtifact>> ListArtifactsAsync(ISpecStore? specs,
        DesignArtifactStore primary, string id, bool byProject, CancellationToken ct = default)
    {
        if (specs is not (ProjectRoutingSpecStore or SpecStore))
            return byProject ? await primary.ListByProjectAsync(id, ct) : await primary.ListBySpecAsync(id, ct: ct);
        if (await ResolveArtifactScopeAsync(specs, id, byProject, ct) is not { } scope) return [];
        var (owner, ids) = scope;
        var owned = await new DesignArtifactStore(owner.Database).ListBySpecsAsync(ids, ct);
        var historical = await new OwnedArtifactHistoryStore(owner.Database).ListDesignsAsync(ids, ct);
        return ArtifactHistory.IncludeUnambiguousHistory(owned, historical, a => a.Id)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray();
    }

    internal static async Task<(SpecStore Owner, IReadOnlyList<string> Ids)?> ResolveArtifactScopeAsync(
        ISpecStore specs, string id, bool byProject, CancellationToken ct)
    {
        var owner = specs is ProjectRoutingSpecStore routing
            ? (byProject ? routing.FindRegisteredStore(id) : routing.ResolveOwner(id)) as SpecStore
            : specs as SpecStore;
        if (owner is null) return null;
        if (!byProject)
        {
            if (await owner.GetAsync(id, ct) is null) return null;
            return (owner, [id]);
        }
        // Legacy single-store installations may keep multiple logical projects together.
        var ids = specs is ProjectRoutingSpecStore ? await owner.ListOwnedIdsAsync(ct)
            : (await owner.ListAsync(id, null, ct)).Select(spec => spec.Id).ToArray();
        return (owner, ids);
    }
}
